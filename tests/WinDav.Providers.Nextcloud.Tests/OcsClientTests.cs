// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using WinDav.Providers.Nextcloud.Ocs;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class OcsClientTests
{
    private static readonly Uri s_server = new("https://cloud.example.com/");

    // The envelope of version 2, with a field the model has no place for. Skipping it is what
    // lets the server say more over time without this program noticing.
    private const string User = """
        {"ocs":{"meta":{"status":"ok","statuscode":200,"message":"OK"},"data":{"id":"ernolf","display-name":"Raphael"}}}
        """;

    [Fact]
    public async Task TheIdentifierIsReadOutOfTheEnvelope()
    {
        OcsHandler handler = new(HttpStatusCode.OK, User);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        Assert.Equal("ernolf", await client.GetUserIdAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheUserIsAskedForAtTheEndpointOfVersionTwo()
    {
        OcsHandler handler = new(HttpStatusCode.OK, User);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        await client.GetUserIdAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://cloud.example.com/ocs/v2.php/cloud/user", handler.Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task ARequestNamesItselfAsOneAndAsksForJson()
    {
        OcsHandler handler = new(HttpStatusCode.OK, User);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        await client.GetUserIdAsync(TestContext.Current.CancellationToken);

        // Without the header the server answers with a login page instead of the API, and
        // some of the endpoints answer XML unless asked otherwise.
        Assert.Equal("true", handler.ApiRequest);
        Assert.Contains("application/json", handler.Accept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInstanceBelowAPathKeepsThatPath()
    {
        OcsHandler handler = new(HttpStatusCode.OK, User);
        using HttpClient httpClient = new(handler);

        // Named without the closing slash, which is how a user writes it down.
        OcsClient client = new(httpClient, new Uri("https://example.com/nextcloud"));
        await client.GetUserIdAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/nextcloud/ocs/v2.php/cloud/user", handler.Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task AFailureStatedInTheEnvelopeIsAFailure()
    {
        // The request arrived and was understood, and the answer is still a refusal.
        const string Refused = """
            {"ocs":{"meta":{"status":"failure","statuscode":997,"message":"Current user is not logged in"},"data":[]}}
            """;

        OcsHandler handler = new(HttpStatusCode.OK, Refused);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetUserIdAsync(TestContext.Current.CancellationToken));

        Assert.Contains("997", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not logged in", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureStatedByTheServerIsAFailure()
    {
        OcsHandler handler = new(HttpStatusCode.Unauthorized, string.Empty);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetUserIdAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task AnEnvelopeWithoutAnIdentifierIsNoAnswer()
    {
        OcsHandler handler = new(HttpStatusCode.OK, """{"ocs":{"meta":{"statuscode":200},"data":{}}}""");
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        await Assert.ThrowsAsync<FormatException>(
            () => client.GetUserIdAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerThatIsNoEnvelopeIsNoAnswer()
    {
        // What an instance answers when the request never reached the API at all.
        OcsHandler handler = new(HttpStatusCode.OK, "<html><body>Log in</body></html>");
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        await Assert.ThrowsAsync<FormatException>(
            () => client.GetUserIdAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAppPasswordIsDeletedWhereTheServerKeepsIt()
    {
        OcsHandler handler = new(HttpStatusCode.OK, """{"ocs":{"meta":{"statuscode":200},"data":[]}}""");
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        await client.DeleteAppPasswordAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal("https://cloud.example.com/ocs/v2.php/core/apppassword", handler.Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task ADeletionTheServerRefusesIsAFailure()
    {
        OcsHandler handler = new(HttpStatusCode.Forbidden, string.Empty);
        using HttpClient httpClient = new(handler);
        OcsClient client = new(httpClient, s_server);

        // A caller removing an account is meant to carry on regardless, which it can only do
        // if it is told what happened.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteAppPasswordAsync(TestContext.Current.CancellationToken));
    }

    // Answers every request the same way and keeps what was asked, since what an OCS request
    // carries matters as much as where it goes.
    private sealed class OcsHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? ApiRequest { get; private set; }

        public string Accept { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            Method = request.Method;
            Uri = request.RequestUri;
            Accept = request.Headers.Accept.ToString();
            ApiRequest = request.Headers.TryGetValues("OCS-APIRequest", out IEnumerable<string>? values)
                ? string.Concat(values)
                : null;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
