// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// What a provider is told before it reaches a store for the first time.
/// </summary>
/// <remarks>
/// Nothing of any transport appears here. A provider that speaks HTTP builds its own client
/// out of this; one that speaks something else builds something else. That is what keeps
/// this project free of a dependency it would never get rid of again.
/// </remarks>
public sealed class ProviderSettings
{
    /// <summary>
    /// Gets the server's base address.
    /// </summary>
    public required Uri Server { get; init; }

    /// <summary>
    /// Gets the user as the store knows them, or <see langword="null"/> where the store has
    /// no notion of one.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Gets the credential, or <see langword="null"/> for a store that is reached without
    /// one.
    /// </summary>
    /// <remarks>
    /// It is passed rather than looked up: where a credential is kept is a decision of the
    /// program that runs, and a provider has no business making it.
    /// </remarks>
    public string? Secret { get; init; }

    /// <summary>
    /// Gets the path on the store that becomes the root of what the provider offers.
    /// </summary>
    /// <remarks>
    /// Everything above it is out of reach, which is what turns one account into several
    /// mounts. It is a path in the form <see cref="RemoteEntry.Path"/> describes.
    /// </remarks>
    public string RemotePath { get; init; } = "/";

    /// <summary>
    /// Gets how the program names itself to the store, or <see langword="null"/> to leave
    /// that to the provider.
    /// </summary>
    public string? UserAgent { get; init; }
}
