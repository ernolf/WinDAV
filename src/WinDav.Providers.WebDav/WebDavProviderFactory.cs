// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;
using WinDav.Dav;

namespace WinDav.Providers.WebDav;

/// <summary>
/// Builds connections to a plain WebDAV store.
/// </summary>
/// <remarks>
/// The server's address and the remote path are all there is to it. A store reached over
/// the bare standard has no endpoint to find and no user area to work out.
/// </remarks>
public sealed class WebDavProviderFactory : DavProviderFactory
{
    /// <summary>
    /// The name this kind of store is written under in a configuration.
    /// </summary>
    public const string ProviderName = "webdav";

    /// <inheritdoc/>
    public override string Name => ProviderName;

    /// <inheritdoc/>
    protected override IStorageProvider CreateProvider(DavClient client, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new WebDavProvider(client, DavPath.ToCollectionUri(settings.Server, settings.RemotePath));
    }
}
