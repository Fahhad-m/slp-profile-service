using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RabotSlp.Api;

namespace RabotSlp.Tests;

public class SlpApiClientTests
{
    private const string TwoValues =
        "[{\"TimestampUTC\":\"2026-01-05T00:00:00.000Z\",\"ValueKwh\":20.745}," +
        "{\"TimestampUTC\":\"2026-01-05T00:15:00.000Z\",\"ValueKwh\":19.9}]";

    private static readonly DateTime From = Utc.At(2026, 1, 5);
    private static readonly DateTime To = Utc.At(2026, 1, 6);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public StubHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        // The last response is repeated for any further request.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(next(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Throttled(int retryAfterSeconds)
    {
        var response = Json(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"RATE_LIMITED\",\"message\":\"Too many requests, please retry later\"}}");
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private static (SlpApiClient Client, StubHandler Stub, List<TimeSpan> Delays) Create(
        int maxRetries,
        params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        var stub = new StubHandler(responses);
        var delays = new List<TimeSpan>();
        var retry = new RetryHandler(
            maxRetries,
            TimeSpan.FromSeconds(1),
            null,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            })
        {
            InnerHandler = stub,
        };

        var http = new HttpClient(retry) { BaseAddress = new Uri("https://example.test/") };
        return (new SlpApiClient(http), stub, delays);
    }

    [Fact]
    public async Task Parses_the_documented_response()
    {
        var (client, _, _) = Create(3, _ => Json(HttpStatusCode.OK, TwoValues));

        var result = await client.GetProfileAsync("h1", From, To);

        Assert.Equal(2, result.Count);
        Assert.Equal(Utc.At(2026, 1, 5, 0, 0), result[0].TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, result[0].TimestampUtc.Kind);
        Assert.Equal(20.745m, result[0].ValueKwh);
        Assert.Equal(Utc.At(2026, 1, 5, 0, 15), result[1].TimestampUtc);
        Assert.Equal(19.9m, result[1].ValueKwh);
    }

    [Fact]
    public async Task Sends_the_profile_id_and_the_range_as_utc_timestamps()
    {
        var (client, stub, _) = Create(3, _ => Json(HttpStatusCode.OK, "[]"));

        await client.GetProfileAsync("h2", From, To);

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/slp/h2", request.RequestUri!.AbsolutePath);
        Assert.Equal(
            "?validFromUtc=2026-01-05T00:00:00Z&validToUtc=2026-01-06T00:00:00Z",
            Uri.UnescapeDataString(request.RequestUri.Query));
    }

    [Fact]
    public async Task An_empty_array_is_a_valid_answer()
    {
        var (client, _, _) = Create(3, _ => Json(HttpStatusCode.OK, "[]"));

        var result = await client.GetProfileAsync("h1", From, To);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Waits_as_long_as_Retry_After_says_and_then_succeeds()
    {
        var (client, stub, delays) = Create(
            3,
            _ => Throttled(2),
            _ => Throttled(2),
            _ => Json(HttpStatusCode.OK, TwoValues));

        var result = await client.GetProfileAsync("h1", From, To);

        Assert.Equal(2, result.Count);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_number_of_retries()
    {
        var (client, stub, delays) = Create(2, _ => Throttled(1));

        var error = await Assert.ThrowsAsync<SlpApiException>(() => client.GetProfileAsync("h1", From, To));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Retries_network_errors_with_backoff()
    {
        var (client, stub, delays) = Create(
            3,
            _ => throw new HttpRequestException("connection reset"),
            _ => throw new HttpRequestException("connection reset"),
            _ => Json(HttpStatusCode.OK, "[]"));

        var result = await client.GetProfileAsync("h1", From, To);

        Assert.Empty(result);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
    }

    [Fact]
    public async Task Retries_server_errors()
    {
        var (client, stub, _) = Create(
            3,
            _ => Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"INTERNAL_ERROR\",\"message\":\"try later\"}}"),
            _ => Json(HttpStatusCode.OK, TwoValues));

        var result = await client.GetProfileAsync("h1", From, To);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task An_unknown_profile_is_reported_and_not_retried()
    {
        var (client, stub, delays) = Create(
            3,
            _ => Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"NOT_FOUND\",\"message\":\"Unknown profile id \\\"h9\\\"\"}}"));

        var error = await Assert.ThrowsAsync<ProfileNotFoundException>(() => client.GetProfileAsync("h9", From, To));

        Assert.Contains("h9", error.Message);
        Assert.Single(stub.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_validation_error_carries_the_message_of_the_api_and_is_not_retried()
    {
        var (client, stub, _) = Create(
            3,
            _ => Json(
                HttpStatusCode.BadRequest,
                "{\"error\":{\"code\":\"VALIDATION_ERROR\",\"message\":\"Invalid request\",\"details\":[{\"path\":\"validToUtc\",\"message\":\"Range must not exceed one calendar month\"}]}}"));

        var error = await Assert.ThrowsAsync<SlpApiException>(() => client.GetProfileAsync("h1", From, To));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("Invalid request", error.Message);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_reported()
    {
        var (client, _, _) = Create(3, _ => Json(HttpStatusCode.OK, "<html>maintenance</html>"));

        await Assert.ThrowsAsync<SlpApiException>(() => client.GetProfileAsync("h1", From, To));
    }

    [Fact]
    public async Task An_interval_without_a_value_is_reported()
    {
        var (client, _, _) = Create(3, _ => Json(HttpStatusCode.OK, "[{\"TimestampUTC\":\"2026-01-05T00:00:00.000Z\"}]"));

        await Assert.ThrowsAsync<SlpApiException>(() => client.GetProfileAsync("h1", From, To));
    }
}
