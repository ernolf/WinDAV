// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

namespace WinDav.Abstractions;

/// <summary>
/// Builds a connection to one kind of store.
/// </summary>
/// <remarks>
/// This is how a provider becomes reachable without being referenced. The layer that reads
/// the configuration holds factories by name and never learns what they construct; the
/// program that runs is the one place where the two meet.
/// </remarks>
public interface IStorageProviderFactory
{
    /// <summary>
    /// Gets the name this kind of store is written under in a configuration, in lower case,
    /// for example <c>webdav</c> or <c>nextcloud</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Builds a connection.
    /// </summary>
    /// <param name="settings">Where the store is and how it is reached.</param>
    /// <returns>
    /// The connection, which the caller disposes. Nothing is sent yet: a store that is
    /// unreachable or a credential that is wrong shows up at the first operation, not here.
    /// </returns>
    IStorageConnection Connect(ProviderSettings settings);
}
