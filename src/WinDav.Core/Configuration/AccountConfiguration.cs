// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Configuration;

/// <summary>
/// One server, reached as one user.
/// </summary>
/// <remarks>
/// An account carries no password. What it holds is a name under which the secret is found
/// in whatever store the platform offers, so a configuration file can be copied, logged or
/// attached to a report without leaking a credential.
/// </remarks>
public sealed class AccountConfiguration
{
    /// <summary>
    /// Gets the name this account is referred to by, unique within the configuration.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the server's base address.
    /// </summary>
    public Uri? Server { get; init; }

    /// <summary>
    /// Gets the name of the provider that speaks to this server.
    /// </summary>
    /// <remarks>
    /// A string rather than an enumeration on purpose. An enumeration would mean the core
    /// lists the vendors it knows, and the core is not allowed to know any of them; a name
    /// leaves room for a provider that is added later without touching this project.
    /// </remarks>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Gets the user as the server knows them, which is the one that appears in the path and
    /// not the display name.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the name the credential is stored under, never the credential itself.
    /// </summary>
    public string? SecretRef { get; init; }
}
