using System.IO;

namespace larp.Configuration;

/// <summary>Local settings for an OpenAI-compatible chat completions provider.</summary>
public sealed record OpenAiCompatibleApiOptions(Uri BaseAddress, string Model, string ApiKey)
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1/";
    public const string DefaultModel = "gpt-5-mini";
    public Uri ChatCompletionsEndpoint => BaseAddress.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
        ? BaseAddress
        : new Uri(BaseAddress, "chat/completions");

    public static OpenAiCompatibleApiOptions? FromEnvironment()
    {
        LoadLocalEnvironmentFile();

        var endpoint = Environment.GetEnvironmentVariable("OUTAGE_AI_BASE_URL");
        var model = Environment.GetEnvironmentVariable("OUTAGE_AI_MODEL");
        var key = Environment.GetEnvironmentVariable("OUTAGE_AI_API_KEY");

        endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultBaseUrl : AddSchemeIfMissing(endpoint.Trim());
        model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        if (!Uri.TryCreate(NormalizeTrailingSlash(endpoint), UriKind.Absolute, out var baseAddress) ||
            (baseAddress.Scheme != Uri.UriSchemeHttps && baseAddress.Scheme != Uri.UriSchemeHttp) ||
            (baseAddress.Scheme == Uri.UriSchemeHttp && !baseAddress.IsLoopback)) return null;
        if (baseAddress.IsLoopback && baseAddress.AbsolutePath == "/")
            baseAddress = new UriBuilder(baseAddress) { Path = "/v1/" }.Uri;
        if (string.Equals(key?.Trim(), "put-your-api-key-here", StringComparison.OrdinalIgnoreCase)) key = null;
        if (string.IsNullOrWhiteSpace(key) && !baseAddress.IsLoopback) return null;

        return new OpenAiCompatibleApiOptions(baseAddress, model, key?.Trim() ?? string.Empty);
    }

    private static string AddSchemeIfMissing(string endpoint)
    {
        if (endpoint.Contains("://", StringComparison.Ordinal)) return endpoint;

        var firstSegment = endpoint.Split('/', 2)[0];
        var localHost = firstSegment.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                        firstSegment.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase) ||
                        firstSegment.StartsWith("127.", StringComparison.Ordinal) ||
                        firstSegment.StartsWith("[::1]", StringComparison.OrdinalIgnoreCase) ||
                        firstSegment.Equals("::1", StringComparison.OrdinalIgnoreCase);
        return (localHost ? "http://" : "https://") + endpoint;
    }

    private static string NormalizeTrailingSlash(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + "/";
    }

    private static void LoadLocalEnvironmentFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(file)) continue;

            foreach (var sourceLine in File.ReadLines(file))
            {
                var line = sourceLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var name = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                    Environment.SetEnvironmentVariable(name, value);
            }

            return;
        }
    }
}
