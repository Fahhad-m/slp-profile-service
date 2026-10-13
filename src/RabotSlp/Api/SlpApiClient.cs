using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RabotSlp.Api;

/// <summary>Typed HttpClient for GET /slp/{id}?validFromUtc=...&amp;validToUtc=...</summary>
public sealed class SlpApiClient : ISlpApiClient
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private readonly HttpClient _http;

    public SlpApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<ProfileInterval>> GetProfileAsync(
        string profileId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var uri = $"slp/{Uri.EscapeDataString(profileId)}"
                  + $"?validFromUtc={Uri.EscapeDataString(Format(fromUtc))}"
                  + $"&validToUtc={Uri.EscapeDataString(Format(toUtc))}";

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateExceptionAsync(response, profileId, cancellationToken).ConfigureAwait(false);
        }

        List<ApiProfileValue>? payload;
        try
        {
            payload = await response.Content
                .ReadFromJsonAsync<List<ApiProfileValue>>(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SlpApiException("The SLP API returned a body that is not the expected JSON array.", response.StatusCode, ex);
        }

        if (payload is null)
        {
            throw new SlpApiException("The SLP API returned an empty body.", response.StatusCode);
        }

        var result = new List<ProfileInterval>(payload.Count);
        foreach (var item in payload)
        {
            if (item.TimestampUtc is null || item.ValueKwh is null)
            {
                throw new SlpApiException("An interval in the SLP API response has no TimestampUTC or ValueKwh.", response.StatusCode);
            }

            result.Add(new ProfileInterval(item.TimestampUtc.Value.UtcDateTime, item.ValueKwh.Value));
        }

        return result;
    }

    private static string Format(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    private static async Task<SlpApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        string profileId,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = ExtractMessage(body);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProfileNotFoundException(profileId, message);
        }

        return new SlpApiException(
            $"SLP API request failed with {(int)response.StatusCode} {response.StatusCode}: {message}",
            response.StatusCode);
    }

    // The API reports errors as {"error": {"code": "...", "message": "..."}}.
    private static string ExtractMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, fall through to the raw body.
        }

        return body.Length > 200 ? body[..200] : body;
    }

    internal sealed class ApiProfileValue
    {
        [JsonPropertyName("TimestampUTC")]
        public DateTimeOffset? TimestampUtc { get; set; }

        [JsonPropertyName("ValueKwh")]
        public decimal? ValueKwh { get; set; }
    }
}
