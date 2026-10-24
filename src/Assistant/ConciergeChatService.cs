using AI_Outage_Concierge.Prediction;

namespace AI_Outage_Concierge.Assistant;

public interface IConciergeChatService
{
    string GetResponse(string message, OutagePrediction? prediction);
}

/// <summary>Default local response until an OpenAI-compatible chat endpoint is configured and connected.</summary>
public sealed class StubConciergeChatService : IConciergeChatService
{
    public string GetResponse(string message, OutagePrediction? prediction) =>
        prediction is null
            ? "Select View my estimate first. I can then help explain the planning estimate. This prototype does not have live outage information."
            : $"Your current planning estimate is {prediction.RiskBand.ToLowerInvariant()} risk with a possible restoration range of {prediction.RestorationRange}. {prediction.Explanation} Follow official utility updates for confirmed information.";
}
