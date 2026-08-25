// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using WinDav.Abstractions;

namespace WinDav.Dav;

/// <summary>
/// Builds the HTTP side of a WebDAV connection, once, for every provider that speaks it.
/// </summary>
/// <remarks>
/// The same split as <see cref="DavStorageProvider"/>: what RFC 4918 and HTTP settle lives
/// here, and a vendor factory says only which provider comes out of it.
/// </remarks>
public abstract class DavProviderFactory : IStorageProviderFactory
{
    // A file system asks for many small things at once, so a single connection would
    // serialise a listing. Unbounded would open a socket per request and look like a flood
    // to anything counting them.
    private const int ConnectionsPerServer = 8;

    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(30);

    // Long enough that a mount does not rebuild its pool all day, short enough that a
    // server which changed address is followed without a restart.
    private static readonly TimeSpan s_connectionLifetime = TimeSpan.FromMinutes(15);

    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler is owned by the HttpClient and the HttpClient by the connection that is returned. Neither transfer is one the rule can follow; the catch covers the only path on which nothing takes ownership.")]
    public IStorageConnection Connect(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        HttpClient httpClient = new(CreateMessageHandler(), disposeHandler: true)
        {
            // What bounds an operation is the caller's token. A timeout on the client
            // covers reading the response body as well, so a large download would be cut
            // off partway through for taking as long as a large download takes.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(settings.UserAgent))
            {
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(settings.UserAgent);
            }

            if (!string.IsNullOrEmpty(settings.Secret))
            {
                httpClient.DefaultRequestHeaders.Authorization = Basic(settings.UserId, settings.Secret);
            }

            return new DavConnection(httpClient, CreateProvider(new DavClient(httpClient), settings));
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the provider the connection hands out.
    /// </summary>
    /// <param name="client">The client the requests go out on.</param>
    /// <param name="settings">Where the store is and how it is reached.</param>
    /// <returns>The provider.</returns>
    protected abstract IStorageProvider CreateProvider(DavClient client, ProviderSettings settings);

    /// <summary>
    /// Builds the handler the requests are sent through.
    /// </summary>
    /// <returns>The handler, which the caller takes ownership of.</returns>
    /// <remarks>
    /// Overridable so a test can put its own in the way. Nothing else has a reason to.
    /// </remarks>
    protected virtual HttpMessageHandler CreateMessageHandler() =>
        new SocketsHttpHandler
        {
            // A redirect that is followed silently is a request that went somewhere else.
            // On MOVE and COPY the destination rides in a header, and nothing rewrites it
            // along the way, so the write would land on the old server while the answer
            // came from the new one. A server that has moved says so once, and the address
            // in the configuration is corrected once.
            AllowAutoRedirect = false,
            ConnectTimeout = s_connectTimeout,
            PooledConnectionLifetime = s_connectionLifetime,
            MaxConnectionsPerServer = ConnectionsPerServer,
        };

    // Sent from the first request rather than waited for. Challenge-response would cost a
    // 401 on every request, and a server that answers an unauthenticated request with 404
    // instead of 401 would never be told who is asking.
    private static AuthenticationHeaderValue Basic(string? userId, string secret)
    {
        byte[] pair = Encoding.UTF8.GetBytes($"{userId}:{secret}");

        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(pair));
    }
}
