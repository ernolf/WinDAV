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
