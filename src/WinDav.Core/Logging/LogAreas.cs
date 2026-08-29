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
}
