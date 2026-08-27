// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Core.Security;

/// <summary>
/// Where credentials are kept.
/// </summary>
/// <remarks>
/// <para>
/// A seam with more than one store behind it. <see cref="DpapiSecretStore"/> is what a
/// plain installation uses; the credential manager is the second, for the profile that
/// roams from one machine to the next. Which of them is used is a decision of the program
/// that runs, and decisions.md 68 says why the file store is the one that came first.
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
