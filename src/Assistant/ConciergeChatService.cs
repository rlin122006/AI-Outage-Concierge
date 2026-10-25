using larp.Prediction;

namespace larp.Assistant;

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
            : !prediction.HasData
                ? $"A data-backed estimate is not available for that location. {prediction.Explanation} Follow official utility updates for confirmed information."
                : $"Your current relative historical damage index is {prediction.RelativeRiskIndex:0} out of 100 ({prediction.RiskBand.ToLowerInvariant()}). {prediction.Explanation} Follow official utility updates for confirmed information.";
}
