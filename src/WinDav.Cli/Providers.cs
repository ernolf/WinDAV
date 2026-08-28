// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Core.Providers;
using WinDav.Providers.Nextcloud;
using WinDav.Providers.WebDav;

namespace WinDav.Cli;

/// <summary>
/// The kinds of store this program knows.
/// </summary>
/// <remarks>
/// Written once. Two lists would mean a provider that can be added and not mounted, or the
/// other way round, and the name in the configuration would be the one to find out with.
/// </remarks>
internal static class Providers
{
    /// <summary>
    /// Builds the registry of everything that can be reached.
    /// </summary>
    /// <returns>The registry.</returns>
    internal static ProviderRegistry All() =>
        new([new NextcloudProviderFactory(), new WebDavProviderFactory()]);
}
