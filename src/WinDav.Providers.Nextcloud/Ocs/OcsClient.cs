// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinDav.Dav;

namespace WinDav.Providers.Nextcloud.Ocs;

/// <summary>
/// Asks a Nextcloud server the questions that are not WebDAV.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints and the envelope they answer in are documented under
/// <see href="https://docs.nextcloud.com/server/latest/developer_manual/client_apis/OCS/ocs-api-overview.html"/>.
/// Version 2 is used throughout: it states the outcome as an HTTP status and repeats it in
/// the envelope, where version 1 answers 200 whatever happened and leaves the outcome to the
/// envelope alone.
/// </para>
/// <para>
/// The client does not own the <see cref="HttpClient"/> it is handed. Base address,
/// authentication, the user agent and the lifetime of the handler belong to whoever built it,
/// as with <see cref="DavClient"/>.
/// </para>
/// </remarks>
public sealed class OcsClient
{
    // Without it the server answers a browser redirect to the login page instead of the API.
    private const string ApiRequestHeader = "OCS-APIRequest";

    // The user behind the credentials the request is sent with, whoever that turns out to be.
    private const string UserPath = "ocs/v2.php/cloud/user";

    // The app password the request is sent with, which is the one it deletes.
    private const string AppPasswordPath = "ocs/v2.php/core/apppassword";

    private static readonly MediaTypeWithQualityHeaderValue s_json = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly Uri _server;

    /// <summary>
    /// Initialises a new instance of the <see cref="OcsClient"/> class.
    /// </summary>
    /// <param name="httpClient">The client the requests go out on.</param>
    /// <param name="server">
    /// The server, <c>https://server/</c> or an instance below a path.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="server"/> is not absolute.</exception>
    public OcsClient(HttpClient httpClient, Uri server)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(server);

        if (!server.IsAbsoluteUri)
        {
            throw new ArgumentException("The server has to be an absolute URI.", nameof(server));
        }

        _httpClient = httpClient;
        _server = DavPath.AsCollection(server);
    }

    /// <summary>
    /// Asks the server which user the credentials belong to.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The user's identifier.</returns>
    /// <remarks>
    /// This is the identifier that goes in a WebDAV path, and it is not what a user types to
    /// log in: an instance can accept an email address for that. What a login answers with is
    /// therefore no substitute for asking.
    /// </remarks>
    /// <exception cref="HttpRequestException">The server refused or reported a failure.</exception>
    /// <exception cref="FormatException">The answer is no envelope, or holds no identifier.</exception>
    public async Task<string> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(_server, UserPath));

        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        OcsResponse? envelope;
        OcsUser? user;

        try
        {
            envelope = await JsonSerializer
                .DeserializeAsync(body, NextcloudJson.Default.OcsResponse, cancellationToken)
                .ConfigureAwait(false);

            // Only an object is a user. A call that failed carries an empty array here.
            user = envelope?.Ocs is { Data.ValueKind: JsonValueKind.Object } payload
                ? JsonSerializer.Deserialize(payload.Data, NextcloudJson.Default.OcsUser)
                : null;
        }
        catch (JsonException exception)
        {
            throw new FormatException($"{request.RequestUri} did not answer in an OCS envelope.", exception);
        }

        ThrowIfNotOk(envelope?.Ocs?.Meta, request.RequestUri);

        string? id = user?.Id;

        return string.IsNullOrEmpty(id)
            ? throw new FormatException($"{request.RequestUri} answered without an identifier for the user.")
            : id;
    }

    /// <summary>
    /// Deletes the app password the requests are sent with.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>A task that completes when the server has dropped the password.</returns>
    /// <remarks>
    /// This is how an account is given back what a login took out: the password stops working
    /// on the server, not only in this program. It is worth doing when an account is removed
    /// and not worth insisting on. A server that refuses leaves a password behind that the
    /// user can still revoke in the web interface, so a caller removing an account is better
    /// off carrying on than stopping over it.
    /// </remarks>
    /// <exception cref="HttpRequestException">The server refused.</exception>
    public async Task DeleteAppPasswordAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Delete, new Uri(_server, AppPasswordPath));

        // The answer is an envelope with an empty payload, so there is nothing in it to read.
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowIfNotOk(OcsStatus? meta, Uri? uri)
    {
        if (meta is null || meta.StatusCode == (int)HttpStatusCode.OK)
        {
            return;
        }

        throw new HttpRequestException(
            $"{uri} answered {meta.StatusCode} in the envelope: {meta.Message}",
            inner: null,
            statusCode: null);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(ApiRequestHeader, "true");

        // Some of the older endpoints answer XML unless asked otherwise.
        request.Headers.Accept.Add(s_json);

        HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return response;
        }

        HttpStatusCode received = response.StatusCode;
        response.Dispose();

        throw new HttpRequestException(
            $"{request.Method} {request.RequestUri} expected 200 OK but the server answered {(int)received}.",
            inner: null,
            statusCode: received);
    }
}
