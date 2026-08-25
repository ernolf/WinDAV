// SPDX-FileCopyrightText: 2026 [ernolf] Raphael Gradenwitz <raphael.gradenwitz@googlemail.com>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using WinDav.Providers.Nextcloud.Login;
using Xunit;

namespace WinDav.Providers.Nextcloud.Tests;

public sealed class LoginFlowClientTests
{
    private static readonly Uri s_server = new("https://cloud.example.com/");

    private static readonly TimeSpan s_atOnce = TimeSpan.FromMilliseconds(1);

    // Names, not secrets: what the server hands out here is worth nothing once the flow has
    // ended, and nothing in this file is worth anything anywhere.
    private const string PollToken = "a-poll-token";

    private const string Start = """
        {"poll":{"token":"a-poll-token","endpoint":"https://cloud.example.com/login/v2/poll"},"login":"https://cloud.example.com/login/v2/flow/an-opaque-name"}
        """;

    private const string Granted = """
        {"server":"https://cloud.example.com","loginName":"ernolf","appPassword":"an-app-password"}
        """;

    [Fact]
    public async Task AFlowBeginsWithAPostToTheServer()
    {
        FlowHandler handler = new((HttpStatusCode.OK, Start));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        await client.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://cloud.example.com/index.php/login/v2", handler.Uri?.AbsoluteUri);

        // Nobody is logged in yet, so there is nothing to send along.
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task AnInstanceBelowAPathKeepsThatPath()
    {
        FlowHandler handler = new((HttpStatusCode.OK, Start));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, new Uri("https://example.com/nextcloud"));

        await client.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/nextcloud/index.php/login/v2", handler.Uri?.AbsoluteUri);
    }

    [Fact]
    public async Task TheStartIsReadIntoAnAddressAndATokenToAskUnder()
    {
        FlowHandler handler = new((HttpStatusCode.OK, Start));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        LoginFlowStart start = await client.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://cloud.example.com/login/v2/flow/an-opaque-name", start.Login.AbsoluteUri);
        Assert.Equal("https://cloud.example.com/login/v2/poll", start.Poll.Endpoint.AbsoluteUri);
        Assert.Equal(PollToken, start.Poll.Token);
    }

    [Fact]
    public async Task AStartWithoutATokenIsNoStart()
    {
        const string Incomplete = """
            {"poll":{"endpoint":"https://cloud.example.com/login/v2/poll"},"login":"https://cloud.example.com/login/v2/flow/x"}
            """;

        FlowHandler handler = new((HttpStatusCode.OK, Incomplete));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        await Assert.ThrowsAsync<FormatException>(
            () => client.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStartWithAnAddressThatIsNotAbsoluteIsNoStart()
    {
        const string Relative = """
            {"poll":{"token":"a-poll-token","endpoint":"/login/v2/poll"},"login":"/login/v2/flow/x"}
            """;

        FlowHandler handler = new((HttpStatusCode.OK, Relative));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        await Assert.ThrowsAsync<FormatException>(
            () => client.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStartTheServerRefusesIsAFailure()
    {
        FlowHandler handler = new((HttpStatusCode.ServiceUnavailable, string.Empty));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task APollCarriesTheTokenAsAForm()
    {
        FlowHandler handler = new((HttpStatusCode.NotFound, string.Empty));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        await client.PollAsync(Poll(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Equal($"token={PollToken}", Assert.Single(handler.Bodies));
    }

    [Fact]
    public async Task APollThatIsNotGrantedYetIsNoCredential()
    {
        FlowHandler handler = new((HttpStatusCode.NotFound, string.Empty));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        // Until the user has granted access there is nothing to hand out, and the server says
        // so with a 404. That is the flow working, not a failure.
        Assert.Null(await client.PollAsync(Poll(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGrantedPollIsReadIntoCredentials()
    {
        FlowHandler handler = new((HttpStatusCode.OK, Granted));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        LoginFlowCredentials? credentials = await client.PollAsync(Poll(), TestContext.Current.CancellationToken);

        Assert.NotNull(credentials);
        Assert.Equal("https://cloud.example.com/", credentials.Server.AbsoluteUri);
        Assert.Equal("ernolf", credentials.LoginName);
        Assert.Equal("an-app-password", credentials.AppPassword);
    }

    [Fact]
    public async Task APollTheServerRefusesIsAFailure()
    {
        FlowHandler handler = new((HttpStatusCode.InternalServerError, string.Empty));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PollAsync(Poll(), TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
    }

    [Fact]
    public async Task WaitingAsksAgainUntilAccessIsGranted()
    {
        FlowHandler handler = new(
            (HttpStatusCode.NotFound, string.Empty),
            (HttpStatusCode.NotFound, string.Empty),
            (HttpStatusCode.OK, Granted));

        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        LoginFlowCredentials credentials = await client.WaitAsync(
            Poll(), s_atOnce, timeout: null, TestContext.Current.CancellationToken);

        Assert.Equal("ernolf", credentials.LoginName);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task WaitingStopsWhenTheTokenCanNoLongerBeGranted()
    {
        FlowHandler handler = new((HttpStatusCode.NotFound, string.Empty));
        using HttpClient httpClient = new(handler);
        LoginFlowClient client = new(httpClient, s_server);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitAsync(
                Poll(), s_atOnce, TimeSpan.Zero, TestContext.Current.CancellationToken));

        // The last ask is worth making: a login granted in the final second is still a login.
        Assert.Single(handler.Bodies);
    }

    private static LoginFlowPoll Poll() => new()
    {
        Endpoint = new Uri("https://cloud.example.com/login/v2/poll"),
        Token = PollToken,
    };

    // Answers with what it was given, in order, and repeats the last answer once it has run
    // out, which is what a poll that is never granted looks like.
    private sealed class FlowHandler(params (HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers = new(answers);

        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? ContentType { get; private set; }

        public string? Authorization { get; private set; }

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ContentType = request.Content?.Headers.ContentType?.MediaType;

            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            (HttpStatusCode status, string body) = _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
