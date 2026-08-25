// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Dav;

namespace WinDav.Providers.WebDav;

/// <summary>
/// A store reached over plain RFC 4918, with nothing of any vendor in it.
/// </summary>
/// <remarks>
/// It adds nothing to <see cref="DavStorageProvider"/>, and that is the point: what the base
/// class does is the standard, so anything a vendor provider adds on top of it can be read as
/// the vendor's own.
/// </remarks>
public sealed class WebDavProvider : DavStorageProvider
{
    /// <summary>
    /// Initialises a new instance of the <see cref="WebDavProvider"/> class.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="baseUri">
    /// The collection the seam's root stands for, as an absolute URI. Everything below it is
    /// reachable, nothing above it is.
    /// </param>
    public WebDavProvider(DavClient client, Uri baseUri)
        : base(client, baseUri)
    {
    }
}
