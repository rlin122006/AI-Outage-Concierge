namespace AI_Outage_Concierge.Prediction;

/// <summary>Normalized point on the prototype map grid; replace with geographic coordinates when GIS is added.</summary>
public sealed record MapCoordinate(double X, double Y);

public sealed record OutagePredictionRequest(
    MapCoordinate? Location,
    string StormConditions,
    string HomeType,
    bool MedicalEquipmentNeed,
    bool AccessibilitySupportNeed,
    bool CriticalFacility);

/// <summary>Customer-safe estimate, separate from confirmed utility status.</summary>
public sealed record OutagePrediction(
    string RiskBand,
    string ConfidenceLabel,
    string RestorationRange,
    string Explanation,
    string PreparationGuidance,
    bool IsFallback = false);

public interface IOutagePredictionService
{
    OutagePrediction Predict(OutagePredictionRequest request);
}

/// <summary>Placeholder until heatmap, network, storm, and restoration data are connected.</summary>
public sealed class StubOutagePredictionService : IOutagePredictionService
{
    public OutagePrediction Predict(OutagePredictionRequest request)
    {
        var locationText = request.Location is null
            ? "No map point is selected, so location-specific risk is unavailable."
            : "A prototype grid point is selected; no heatmap is connected yet.";
        var needsText = request.MedicalEquipmentNeed || request.AccessibilitySupportNeed || request.CriticalFacility
            ? " Your declared support needs should be considered when preparing and communicating."
            : string.Empty;
        return new OutagePrediction(
            "Moderate",
            "Illustrative only",
            "6–18 hours",
            $"{locationText} Selected storm scenario: {request.StormConditions}.{needsText} Values are placeholders, not a data-backed forecast.",
            request.MedicalEquipmentNeed
                ? "Contact your healthcare provider about backup power, keep essential equipment charged, and identify a support contact. If someone is in immediate danger, call emergency services."
                : "Charge essential devices, keep flashlights and water available, and review your household outage plan.");
    }
}
