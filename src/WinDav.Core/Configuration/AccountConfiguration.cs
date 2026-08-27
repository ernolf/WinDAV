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
    /// Gets what this account is, as opposed to what it is called.
    /// </summary>
    /// <remarks>
    /// It is never made up and never changes. Everything that has to survive a renaming points
    /// here, which is what lets <see cref="Id"/> be a name a person chose. The word is the
    /// one from the standard rather than the Windows one, because a member named Guid is a
    /// member CA1720 turns down. See decisions.md 71.
    /// </remarks>
    public Guid Uuid { get; init; }

    /// <summary>
    /// Gets the name this account is referred to by, unique within the configuration.
    /// </summary>
    /// <remarks>
    /// A name, not an identity: it is derived from the login and the server, it can be given
    /// with <c>--id</c>, and it may change. <see cref="Uuid"/> is what stays.
    /// </remarks>
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
    /// Gets the user as the server itself knows them, which is the name in the path.
    /// </summary>
    /// <remarks>
    /// The canonical one, not the display name and not necessarily what was typed to log in:
    /// the file tree on the server is named after it. Where it is an email address, that is
    /// what stands in the path. See decisions.md 71.
    /// </remarks>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the spelling the credential is accepted under, or <see langword="null"/> when it
    /// is the same as <see cref="UserId"/>.
    /// </summary>
    /// <remarks>
    /// A server may let one account in under more than one name, and it keeps the one that
    /// was used in the record of the password it issued. Any other spelling of the same
    /// account is turned down, which is why this is kept apart from the name in the path.
    /// </remarks>
    public string? LoginId { get; init; }

    /// <summary>
    /// Gets the key the credential is stored under, never the credential itself.
    /// </summary>
    /// <remarks>
    /// A key of the program's own making and of no meaning, not the id of the account. A name
    /// that says something is a name that changes, and two accounts that arrive at the same
    /// one would arrive at the same credential. See decisions.md 70.
    /// </remarks>
    public string? SecretRef { get; init; }

    /// <summary>
    /// Gets a value indicating whether the server handed out what is kept under
    /// <see cref="SecretRef"/>, rather than a person typing it in.
    /// </summary>
    /// <remarks>
    /// What came out of a login is of use to nothing else, because it is never shown again,
    /// so removing the account is the moment to give it back. What was typed in belongs to
    /// whoever typed it and may be in use elsewhere, so it is asked about. The name says
    /// nothing about credentials on purpose; there is one place for those, and it is not
    /// here. See decisions.md 69.
    /// </remarks>
    public bool IssuedHere { get; init; }
}
