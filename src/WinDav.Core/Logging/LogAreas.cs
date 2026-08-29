// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Logging;

/// <summary>
/// The area a record belongs to, and what it is called in a file.
/// </summary>
/// <remarks>
/// The namespace a record was written in decides its area. That is how the logging of .NET
/// itself is filtered, and it keeps the area out of every call site: a class is where it is,
/// and its area follows from that. The namespaces are spelt out here for the reason
/// <see cref="ProductInfo"/> gives for spelling them out everywhere else.
/// </remarks>
public static class LogAreas
{
    // Read in order, so the narrower prefix has to come before the wider one it sits under.
    private static readonly (string Prefix, LogArea Area)[] s_prefixes =
    [
        ("WinDav.Fs.", LogArea.Fs),
        ("WinDav.Dav.", LogArea.Http),
        ("WinDav.Providers.", LogArea.Provider),
        ("WinDav.Core.Providers.", LogArea.Provider),
    ];

    /// <summary>
    /// Gets every area there is, in the order they are listed in a message to a person.
    /// </summary>
    /// <remarks>
    /// A recording that names none is a recording of all of them, and this is that list.
    /// </remarks>
    public static IReadOnlyList<LogArea> All { get; } =
    [
        LogArea.Fs,
        LogArea.Http,
        LogArea.Provider,
        LogArea.Cli,
    ];

    /// <summary>
    /// Reads the area out of the name a logger was created under.
    /// </summary>
    /// <param name="categoryName">The category, which is the full name of a type.</param>
    /// <returns>The area, and <see cref="LogArea.Cli"/> for a name none of the prefixes fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="categoryName"/> is null.</exception>
    public static LogArea Of(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        foreach ((string prefix, LogArea area) in s_prefixes)
        {
            if (categoryName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return area;
            }
        }

        // Everything left is a command doing its work: reading the configuration, opening a
        // credential, deciding what to mount. That is what cli means, and it is why nothing
        // has to be registered for a new class to be logged.
        return LogArea.Cli;
    }

    /// <summary>
    /// Gets the name an area is written under, which is the name it is switched on by.
    /// </summary>
    /// <param name="area">The area.</param>
    /// <returns>The lower-case name.</returns>
    public static string Name(LogArea area) => area switch
    {
        LogArea.Fs => "fs",
        LogArea.Http => "http",
        LogArea.Provider => "provider",
        _ => "cli",
    };

    /// <summary>
    /// Reads an area from the name it is written under.
    /// </summary>
    /// <param name="name">The name, in any case.</param>
    /// <param name="area">The area, when the name is one.</param>
    /// <returns><see langword="true"/> when the name is one of the four.</returns>
    /// <remarks>
    /// The names a person types on the command line are the names they read in the file. One
    /// spelling, learnt once.
    /// </remarks>
    public static bool TryParse(string? name, out LogArea area)
    {
        foreach (LogArea candidate in All)
        {
            if (string.Equals(Name(candidate), name, StringComparison.OrdinalIgnoreCase))
            {
                area = candidate;

                return true;
            }
        }

        area = LogArea.Cli;

        return false;
    }
}
