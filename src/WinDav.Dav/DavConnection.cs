// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using WinDav.Abstractions;

namespace WinDav.Dav;

/// <summary>
/// A provider together with the <see cref="HttpClient"/> its requests go out on.
/// </summary>
/// <remarks>
/// <see cref="DavClient"/> does not own the client it is handed, and a provider does not
/// either. This is where that ownership ends up, so that closing a mount closes the sockets
/// it opened.
/// </remarks>
public sealed class DavConnection : IStorageConnection
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initialises a new instance of the <see cref="DavConnection"/> class.
    /// </summary>
    /// <param name="httpClient">The client to take ownership of.</param>
    /// <param name="provider">The provider that sends its requests on it.</param>
    public DavConnection(HttpClient httpClient, IStorageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(provider);

        _httpClient = httpClient;
        Provider = provider;
    }

    /// <inheritdoc/>
    public IStorageProvider Provider { get; }

    /// <summary>
    /// Closes the connection. The provider is unusable afterwards.
    /// </summary>
    public void Dispose()
    {
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}
