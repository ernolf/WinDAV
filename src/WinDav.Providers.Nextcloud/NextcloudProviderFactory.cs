// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Dav;

namespace WinDav.Providers.Nextcloud;

/// <summary>
/// Builds connections to a Nextcloud server.
/// </summary>
/// <remarks>
/// A configuration holds the address a person types into a browser. Where the DAV endpoint
/// sits below it, and where a user's files and uploads sit below that, is
/// <see cref="NextcloudProvider.ForServer"/>'s business.
/// </remarks>
public sealed class NextcloudProviderFactory : DavProviderFactory
{
    /// <summary>
    /// The name this kind of store is written under in a configuration.
    /// </summary>
    public const string ProviderName = "nextcloud";

    /// <inheritdoc/>
    public override string Name => ProviderName;

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">
    /// <see cref="ProviderSettings.UserId"/> is missing. A Nextcloud file path has the user
    /// in it, so there is no such thing as reaching one without knowing who is asking.
    /// </exception>
    protected override IStorageProvider CreateProvider(DavClient client, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? userId = settings.UserId;

        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("A Nextcloud account needs a user id.", nameof(settings));
        }

        return NextcloudProvider.ForServer(client, settings.Server, userId, settings.RemotePath);
    }
}
