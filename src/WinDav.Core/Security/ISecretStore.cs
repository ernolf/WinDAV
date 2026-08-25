// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Security;

/// <summary>
/// Where credentials are kept.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than an implementation, and deliberately so: the store worth having on
/// Windows is the credential manager, and reaching it needs a Windows target framework,
/// which this project does not have. The program that runs supplies one.
/// </para>
/// <para>
/// What a configuration holds is the reference — <see cref="Configuration.AccountConfiguration.SecretRef"/> —
/// and nothing else. The credential itself never passes through the configuration file.
/// </para>
/// </remarks>
public interface ISecretStore
{
    /// <summary>
    /// Reads a credential.
    /// </summary>
    /// <param name="reference">The name it is stored under.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The credential, or <see langword="null"/> when nothing is stored under that name.</returns>
    Task<string?> GetAsync(string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a credential, replacing whatever was under that name.
    /// </summary>
    /// <param name="reference">The name to store it under.</param>
    /// <param name="secret">The credential.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when it is stored.</returns>
    Task SetAsync(string reference, string secret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a credential. A name that is not there is not an error.
    /// </summary>
    /// <param name="reference">The name to remove.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    /// <returns>A task that completes when the name is gone.</returns>
    Task RemoveAsync(string reference, CancellationToken cancellationToken = default);
}
