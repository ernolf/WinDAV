// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Cli;

/// <summary>
/// Reads the address a person typed for a server.
/// </summary>
/// <remarks>
/// In one place because two commands take one, and a rule about what counts as an address
/// that is written twice is a rule that differs between them as soon as one is changed.
/// </remarks>
internal static class ServerAddress
{
    /// <summary>
    /// Tells something that was meant as an address from something that was meant as a name.
    /// </summary>
    /// <param name="written">What was typed.</param>
    /// <returns>Whether it was meant as an address.</returns>
    /// <remarks>
    /// By the scheme in front of it and by nothing else, so that a person can tell the two
    /// apart the same way: what carries a scheme is an address, whether or not it is one this
    /// program takes, and <see cref="Read"/> is what says which. A name that is not an address
    /// is the name of a mount; decisions.md 73.
    /// </remarks>
    internal static bool LooksLikeOne(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        return written.Contains(Uri.SchemeDelimiter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads an address, and refuses anything that is not one.
    /// </summary>
    /// <param name="address">What was typed.</param>
    /// <returns>The server.</returns>
    /// <exception cref="UsageException">It is not an absolute http or https address.</exception>
    internal static Uri Read(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? server)
            || (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                && !string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)))
        {
            throw new UsageException($"'{address}' is not an http or https address.");
        }

        return server;
    }
}
