// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Login;

/// <summary>
/// What a server answers when a login has been begun: where to send the user, and where to
/// ask whether they are done.
/// </summary>
public sealed class LoginFlowStart
{
    /// <summary>
    /// Gets the address to open in the user's own browser. Not in a view of this program's
    /// own: whatever it takes to log in there - a proxy, a client certificate, a second
    /// factor, a password manager - is already set up in the browser and would have to be set
    /// up again here.
    /// </summary>
    public required Uri Login { get; init; }

    /// <summary>Gets what the login is asked about while the user is at it.</summary>
    public required LoginFlowPoll Poll { get; init; }
}
