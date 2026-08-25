// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Providers.Nextcloud.Login;

/// <summary>
/// Where a login in progress is asked about, and under which name.
/// </summary>
/// <remarks>
/// Both values are the server's, handed back as they arrived. The token stands for the login
/// and not for the user: it is worth nothing once the flow has ended, and it is what an
/// onlooker would need to collect the credentials in the client's place, so it is not
/// something to write down.
/// </remarks>
public sealed class LoginFlowPoll
{
    /// <summary>Gets the address the poll requests go to.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Gets the name this login goes by while it is being polled for.</summary>
    public required string Token { get; init; }
}
