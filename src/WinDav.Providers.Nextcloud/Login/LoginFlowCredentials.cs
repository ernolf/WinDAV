// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Login;

/// <summary>
/// What a granted login is worth: an address, a name, and a password that belongs to this
/// program alone.
/// </summary>
/// <remarks>
/// The user's own password never appears here, and the server hands these out once. What is
/// done with them afterwards is the caller's: the password belongs in a secret store, and the
/// configuration file gets the name it is stored under, never the password itself.
/// </remarks>
public sealed class LoginFlowCredentials
{
    /// <summary>
    /// Gets the server the credentials are for, as the server named itself. It can differ
    /// from what the user typed, which is why it is worth keeping.
    /// </summary>
    public required Uri Server { get; init; }

    /// <summary>
    /// Gets the name the user logged in with. An instance can accept an email address for
    /// that, so this is not necessarily the identifier a WebDAV path is built from; see
    /// <see cref="Ocs.OcsClient.GetUserIdAsync(CancellationToken)"/>.
    /// </summary>
    public required string LoginName { get; init; }

    /// <summary>
    /// Gets the password to send with every later request. It is one of several the user can
    /// hold, and revoking it in the web interface costs them nothing else.
    /// </summary>
    public required string AppPassword { get; init; }
}
