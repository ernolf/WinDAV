// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using WinDav.Core.Providers;

namespace WinDav.Fs;

/// <summary>
/// What walked the mount, what it asked for in vain, and what the shell has registered, in
/// one block written when the mount comes down.
/// </summary>
/// <remarks>
/// Two of the three parts exist only inside the running process, so the mount writes them
/// itself rather than answering a command that asks from outside. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#84-the-mount-says-who-walked-it-and-what-the-shell-has-registered">decision 84</see>.
/// </remarks>
public static class MountReport
{
    /// <summary>
    /// How many absent names are printed. A program that goes through a thousand of them
    /// would otherwise turn the report into a log of its own.
    /// </summary>
    public const int Names = 20;

    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <param name="walkers">Who opened something, from the file system.</param>
    /// <param name="absences">
    /// What was asked for and found in no directory it was asked for in, or
    /// <see langword="null"/> where nothing kept that count.
    /// </param>
    /// <param name="overlays">What the shell has registered.</param>
    /// <returns>The whole report, one line per entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="walkers"/> or <paramref name="overlays"/> is null.</exception>
    public static string Build(
        IReadOnlyList<Walker> walkers,
        IReadOnlyList<AbsentName>? absences,
        IReadOnlyList<ShellOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(walkers);
        ArgumentNullException.ThrowIfNull(overlays);

        StringBuilder report = new();

        report.Append("What walked this mount.");

        Walked(report, walkers);
        Absent(report, absences);
        Registered(report, overlays);

        return report.ToString();
    }

    private static void Walked(StringBuilder report, IReadOnlyList<Walker> walkers)
    {
        report.Append("\n  Who opened something");

        if (walkers.Count == 0)
        {
            report.Append("\n    Nobody. Nothing on this mount was opened.");

            return;
        }

        foreach (Walker walker in walkers)
        {
            report.Append(CultureInfo.InvariantCulture, $"\n    {walker.Program} ({walker.ProcessId}): ")
                .Append(CultureInfo.InvariantCulture, $"{walker.Opened} opened, ")
                .Append(CultureInfo.InvariantCulture, $"{walker.Directories} of them directories, ")
                .Append(CultureInfo.InvariantCulture, $"{walker.Waited.TotalMilliseconds:F0} ms waited");
        }
    }

    private static void Absent(StringBuilder report, IReadOnlyList<AbsentName>? absences)
    {
        report.Append("\n  What was asked for and never found");

        if (absences is null)
        {
            report.Append("\n    Not counted: this mount holds no listings.");

            return;
        }

        if (absences.Count == 0)
        {
            report.Append("\n    Nothing. Every name that was asked for was there.");

            return;
        }

        int printed = Math.Min(Names, absences.Count);

        for (int index = 0; index < printed; index++)
        {
            AbsentName name = absences[index];

            report.Append(CultureInfo.InvariantCulture, $"\n    {name.Name}: {name.Asked} asked, ")
                .Append(Listings(name.Listings))
                .Append(" bought");
        }

        if (printed == absences.Count)
        {
            return;
        }

        int asked = 0;
        int listings = 0;

        for (int index = printed; index < absences.Count; index++)
        {
            asked += absences[index].Asked;
            listings += absences[index].Listings;
        }

        report.Append(CultureInfo.InvariantCulture, $"\n    and {absences.Count - printed} more names: ")
            .Append(CultureInfo.InvariantCulture, $"{asked} asked, ")
            .Append(Listings(listings))
            .Append(" bought");
    }

    private static void Registered(StringBuilder report, IReadOnlyList<ShellOverlay> overlays)
    {
        report.Append("\n  What the shell has registered to draw over icons");

        if (overlays.Count == 0)
        {
            report.Append("\n    Nothing, or the registry could not be read.");

            return;
        }

        bool marked = false;

        for (int index = 0; index < overlays.Count; index++)
        {
            ShellOverlay overlay = overlays[index];

            if (!overlay.Loaded && !marked)
            {
                report.Append(
                    CultureInfo.InvariantCulture,
                    $"\n    -- Windows loads the first {ShellOverlays.Loads}; what follows is registered and never asked --");

                marked = true;
            }

            report.Append(CultureInfo.InvariantCulture, $"\n    {index + 1,2}. '{overlay.Name}' {overlay.Clsid} ")
                .Append(Module(overlay));
        }
    }

    private static string Module(ShellOverlay overlay)
    {
        if (overlay.Module is null)
        {
            return "no server registered for that class";
        }

        if (overlay.Present is not bool present)
        {
            return $"{overlay.Module} (registered by name, not by path)";
        }

        if (!present)
        {
            return $"{overlay.Module} (missing)";
        }

        return overlay.Vendor is null ? overlay.Module : $"{overlay.Module} ({overlay.Vendor})";
    }

    private static string Listings(int listings) =>
        listings == 1 ? "1 listing" : string.Create(CultureInfo.InvariantCulture, $"{listings} listings");
}
