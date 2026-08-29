// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;

namespace WinDav.Core.Logging;

/// <summary>
/// What a log must not carry, and what stands there instead.
/// </summary>
/// <remarks>
/// <para>
/// Decision 60 keeps a password from being handed around at all, which leaves the log as the
/// one place it could still surface. Nothing here guesses: each method is for one shape that
/// is known to be able to hold a secret, and everything else is written out as it is. Paths
/// and file names in particular are written out, because without them a record is worthless.
/// </para>
/// <para>
/// The marker is the wording Nextcloud uses in its own reports, so a reader who has seen one
/// knows at a glance what it means, and so that it can never be mistaken for a value.
/// </para>
/// </remarks>
public static class LogRedaction
{
    /// <summary>
    /// What stands where something sensitive was.
    /// </summary>
    public const string Marker = "***REMOVED SENSITIVE VALUE***";

    // What a credential travels in over HTTP. Authorization is on every request this product
    // sends; the rest are here because a server may answer with them and because a proxy in
    // front of one may ask for its own.
    private static readonly string[] s_secretHeaders =
    [
        "Authorization",
        "Proxy-Authorization",
        "WWW-Authenticate",
        "Proxy-Authenticate",
        "Cookie",
        "Set-Cookie",
    ];

    /// <summary>
    /// Writes an address without what may be a credential in it.
    /// </summary>
    /// <param name="server">The address.</param>
    /// <returns>
    /// The address, with the marker in place of the user information and in place of the
    /// query, and without the fragment.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is null.</exception>
    public static string Server(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);

        // A relative address has no room for a credential, and nothing to take apart.
        if (!server.IsAbsoluteUri)
        {
            return server.ToString();
        }

        StringBuilder text = new();

        text.Append(server.Scheme).Append("://");

        if (server.UserInfo.Length > 0)
        {
            // Kept in shape rather than dropped: that a credential was written into the
            // address is itself worth reading, and it is a thing people do.
            text.Append(Marker).Append('@');
        }

        text.Append(server.Authority).Append(server.AbsolutePath);

        if (server.Query.Length > 1)
        {
            // A token handed out by a login flow travels here.
            text.Append('?').Append(Marker);
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes what a header carries, or the marker when the header is one that carries a
    /// credential.
    /// </summary>
    /// <param name="name">The name of the header.</param>
    /// <param name="values">What it carries.</param>
    /// <returns>The values, or the marker.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <remarks>
    /// By name and not by looking at the value: every request this product sends carries a
    /// password in Authorization, and a rule that has to recognise one is a rule that will
    /// one day fail to.
    /// </remarks>
    public static string Header(string? name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return IsSecret(name) ? Marker : string.Join(", ", values);
    }

    /// <summary>
    /// Tells whether a header is one of those that carry a credential.
    /// </summary>
    /// <param name="name">The name of the header.</param>
    /// <returns>Whether what it carries has to be taken out.</returns>
    public static bool IsSecret(string? name)
    {
        foreach (string secret in s_secretHeaders)
        {
            if (string.Equals(secret, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes the command that was run, for the first lines of a file.
    /// </summary>
    /// <param name="arguments">What the program was started with, without the program itself.</param>
    /// <returns>
    /// The command, with every argument that is an address written as <see cref="Server(Uri)"/>
    /// writes one.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public static string CommandLine(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        StringBuilder text = new(ProductInfo.Slug);

        foreach (string argument in arguments)
        {
            text.Append(' ').Append(Argument(argument));
        }

        return text.ToString();
    }

    // A password is never on the command line, because decision 60 has it asked for. An
    // address can be, and one written with a credential in it is the one way a secret gets
    // there anyway.
    //
    // Only http and https count as one. Uri reads C:\icons\cloud.ico and even N: as a file
    // address, and rewriting either of them would turn a readable command line into a puzzle.
    private static string Argument(string argument) =>
        Uri.TryCreate(argument, UriKind.Absolute, out Uri? address) && IsWeb(address)
            ? Server(address)
            : argument;

    private static bool IsWeb(Uri address) =>
        string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
        || string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
}
