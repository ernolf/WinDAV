// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using WinDav.Abstractions;
using WinDav.Core;
using WinDav.Providers.Nextcloud;
using WinDav.Providers.Nextcloud.Ocs;

namespace WinDav.Cli;

/// <summary>
/// What this program asks a Nextcloud server before a provider is in the way.
/// </summary>
/// <remarks>
/// Adding an account and mounting one that was typed out both need the same two things: a
/// client that names this program, and the canonical user id behind a login. They are here
/// rather than in either command, because the same call written twice is two calls as soon
/// as one of them is changed. See
/// <see href="https://github.com/ernolf/WinDAV/wiki/Decisions#72-mount-takes-an-account">decision 72</see>.
/// </remarks>
internal static class NextcloudServer
{
    /// <summary>
    /// Tells whether a provider is the one these calls are addressed to.
    /// </summary>
    /// <param name="provider">The name a provider is registered under.</param>
    /// <returns>Whether it is Nextcloud.</returns>
    internal static bool IsNextcloud(string provider) =>
        string.Equals(provider, NextcloudProviderFactory.ProviderName, StringComparison.Ordinal);

    /// <summary>
    /// Asks the server what the user behind a login is called.
    /// </summary>
    /// <param name="server">The server to ask.</param>
    /// <param name="loginId">The name the credential is presented under.</param>
    /// <param name="secret">The credential.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The canonical user id, which is what goes into a path.</returns>
    /// <remarks>
    /// Decision 71: the name in <c>remote.php/dav/files/&lt;userId&gt;</c> is the server's own
    /// and not always the one the password was issued for.
    /// </remarks>
    internal static async Task<string> ResolveUserIdAsync(
        Uri server,
        string loginId,
        string secret,
        CancellationToken cancellationToken)
    {
        using HttpClient httpClient = Authenticated(loginId, secret);

        try
        {
            return await new OcsClient(httpClient, server).GetUserIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException failure)
        {
            throw AsFailure(server, failure);
        }
        catch (FormatException answer)
        {
            throw new ProviderException(ProviderError.Protocol, $"The server is {server.Host}.", answer);
        }
    }

    /// <summary>
    /// Builds a client that presents a credential.
    /// </summary>
    /// <param name="loginId">The name the credential is presented under.</param>
    /// <param name="secret">The credential.</param>
    /// <returns>The client, which the caller disposes.</returns>
    internal static HttpClient Authenticated(string loginId, string secret)
    {
        HttpClient httpClient = Anonymous();

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{loginId}:{secret}")));

        return httpClient;
    }

    /// <summary>
    /// Builds a client that presents nothing but the name of this program.
    /// </summary>
    /// <returns>The client, which the caller disposes.</returns>
    internal static HttpClient Anonymous()
    {
        HttpClient httpClient = new();

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"{ProductInfo.Name}/{ProductInfo.Version}");

        return httpClient;
    }

    /// <summary>
    /// Turns a failed request into one of the cases the program has a sentence for.
    /// </summary>
    /// <param name="server">The server that was asked.</param>
    /// <param name="failure">What went wrong.</param>
    /// <returns>The failure to throw.</returns>
    /// <remarks>
    /// These calls go out without a provider in between, and a provider is what would
    /// otherwise do this.
    /// </remarks>
    internal static ProviderException AsFailure(Uri server, HttpRequestException failure)
    {
        ProviderError error = failure.StatusCode switch
        {
            null => ProviderError.Unreachable,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderError.PermissionDenied,
            _ => ProviderError.Protocol,
        };

        return new ProviderException(error, $"The server is {server.Host}.", failure);
    }
}
