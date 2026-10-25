namespace larp.Configuration;

/// <summary>Settings contract for a future OpenAI-compatible chat completion provider.</summary>
public sealed record OpenAiCompatibleApiOptions(Uri BaseAddress, string Model, string ApiKey)
{
    public static OpenAiCompatibleApiOptions? FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("OUTAGE_AI_BASE_URL");
        var model = Environment.GetEnvironmentVariable("OUTAGE_AI_MODEL");
        var key = Environment.GetEnvironmentVariable("OUTAGE_AI_API_KEY");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseAddress) ||
            string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(key)) return null;
        return new OpenAiCompatibleApiOptions(baseAddress, model, key);
    }
}
