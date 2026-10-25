using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using larp.Configuration;
using larp.Prediction;

namespace larp.Assistant;

/// <summary>Calls an OpenAI-compatible Chat Completions endpoint and keeps a short local conversation.</summary>
public sealed class OpenAiCompatibleConciergeChatService : IConciergeChatService
{
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly OpenAiCompatibleApiOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly List<ChatMessage> _history = [];

    public OpenAiCompatibleConciergeChatService(OpenAiCompatibleApiOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<string> GetResponseAsync(string message, OutagePrediction? prediction, CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken);
        try
        {
            var messages = new List<ChatMessage>
            {
                new("system", BuildSystemPrompt(prediction))
            };
            messages.AddRange(_history);
            messages.Add(new ChatMessage("user", message));

            var payload = JsonSerializer.Serialize(new ChatCompletionRequest(_options.Model, messages));
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsEndpoint);
            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ConciergeChatException(DescribeFailure(response.StatusCode));

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            var answer = ReadAssistantText(document.RootElement);
            if (string.IsNullOrWhiteSpace(answer))
                throw new ConciergeChatException("The assistant returned an empty response. Try asking again.");

            _history.Add(new ChatMessage("user", message));
            _history.Add(new ChatMessage("assistant", answer));
            if (_history.Count > 24) _history.RemoveRange(0, _history.Count - 24);
            return answer.Trim();
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private static string BuildSystemPrompt(OutagePrediction? prediction)
    {
        var context = prediction is null
            ? "No estimate has been calculated yet. Ask the customer to select a location and press View my estimate before discussing local results."
            : !prediction.HasData
                ? $"The latest estimate has no supporting data for the selected area. Do not estimate a risk there. Explanation: {prediction.Explanation}"
                : $"Latest estimate: relative damage index {prediction.RelativeRiskIndex:0}/100; band {prediction.RiskBand}; local support {prediction.LocalDataSupport:P0}. Restoration scenario: {prediction.RestorationRange}. Network context: {prediction.NetworkSummary} Explanation: {prediction.Explanation}";

        return "You are the L.A.R.P. outage-preparation assistant. Give concise, plain-language answers about the customer's estimate and practical preparation. " +
               "The relative damage index is a historical planning score, not an outage probability, confirmed outage, or live utility forecast. " +
               "Never claim live outage status or invent utility restoration estimates. Make clear when local data is unavailable and direct customers to official utility updates. " +
               "Treat safety as urgent: advise people to stay away from downed lines and call emergency services and their utility; for immediate danger, call emergency services. " +
               "Use only the context provided here and information the customer states; do not infer sensitive needs. " + context;
    }

    private static string ReadAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new ConciergeChatException("The provider response did not contain a chat completion.");

        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var responseMessage) || !responseMessage.TryGetProperty("content", out var content))
            throw new ConciergeChatException("The provider response did not contain assistant text.");

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        return string.Join("", content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString())
            .Where(text => !string.IsNullOrEmpty(text)));
    }

    private static string DescribeFailure(System.Net.HttpStatusCode statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "The API rejected the key. Check OUTAGE_AI_API_KEY in your local .env file.",
        System.Net.HttpStatusCode.TooManyRequests => "The API rate limit or account quota was reached. Wait a moment and try again.",
        System.Net.HttpStatusCode.NotFound => "The API endpoint was not found. Check OUTAGE_AI_BASE_URL in your local .env file.",
        _ => $"The AI provider returned HTTP {(int)statusCode} ({statusCode}). Check the model and endpoint settings, then try again."
    };

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);
}

public sealed class ConciergeChatException(string message) : Exception(message);
