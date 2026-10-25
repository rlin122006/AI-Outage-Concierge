using System.Globalization;
using System.IO;

namespace larp.Prediction;

public sealed record MapCoordinate(double Longitude, double Latitude);
public sealed record StormScenario(string Name, double Multiplier);
/// <summary>Null override means derive the factor from nearby overhead/underground GIS mix.</summary>
public sealed record AssetExposure(string Name, double? OverrideMultiplier);
public sealed record RepairAccessCondition(string Name, double DirectHoursAdderPerJob);

public sealed record OutagePredictionRequest(
    MapCoordinate Location,
    StormScenario Storm,
    AssetExposure Exposure,
    int CrewSize,
    RepairAccessCondition Access,
    bool MedicalEquipmentNeed,
    bool AccessibilitySupportNeed,
    bool CriticalFacility);

/// <summary>Sanitized network and damage statistics in one approximately 1 km map cell.</summary>
public sealed record NetworkHeatmapCell(
    double Longitude, double Latitude,
    double Premises, double OverheadPremises,
    double PrimaryKilometers, double PrimaryOverheadKilometers,
    double SecondaryKilometers, double SecondaryOverheadKilometers,
    double Poles, double SmallPoles, double MediumPoles, double LargePoles,
    double Transformers, double Fuses, double Switches, double Reclosers,
    double TransformerPremiseLinks, double DevicePremiseLinks,
    double DamageReports, double DamageScore,
    double PoleDamage, double WireDamage, double FuseDamage, double SwitchDamage,
    double RecloserDamage, double TransformerDamage, double OtherDamage,
    // Prepared from workbook install+removal averages. Pole reports use nearest pole size and
    // nearest primary conductor phase count, plus representative crossarm/insulator/pin/dead-end
    // units; wire reports use the secondary-conductor attachment average per phase.
    double DirectLaborHours)
{
    public double PrimaryConductorKilometers => PrimaryKilometers;
    public double SecondaryConductorKilometers => SecondaryKilometers;
    public double SectionalizingDevices => Fuses + Switches + Reclosers;
}

public sealed record OutagePrediction(
    bool HasData,
    string RiskBand,
    double? RelativeRiskIndex,
    double? BaseRiskIndex,
    double? LocalDataSupport,
    double StormMultiplier,
    double ExposureMultiplier,
    double OverheadShare,
    int NearbyDamageReports,
    string NetworkSummary,
    string RestorationRange,
    double? TotalPersonHours,
    string Explanation,
    string PreparationGuidance)
{
    public static OutagePrediction NoData(string reason, bool medicalNeed) => new(
        false, "Not available", null, null, null, 1, 1, 0, 0,
        "No nearby GIS assets or damage records were found in the supported dataset.",
        "Not available", null, reason,
        medicalNeed
            ? "Because this location is outside the data coverage, the app cannot estimate local risk. Contact your care team or local emergency management about backup-power planning."
            : "Because this location is outside the data coverage, use official utility and weather updates to plan.");
}

public interface IOutagePredictionService
{
    OutagePrediction Predict(OutagePredictionRequest request);
}

/// <summary>
/// Uses anonymized spatial summaries of the supplied GIS, topology and ARCOS damage layers.
/// The 0–100 result is a relative index, not a probability: the challenge extract has damage
/// observations but no unaffected-asset or storm-specific denominator.
/// </summary>
public sealed class HistoricalDamagePredictionService : IOutagePredictionService
{
    public const double SearchRadiusKilometers = 2.0;
    public const double IndirectLaborMultiplier = 4.5;
    private const double KernelBandwidthKilometers = 0.8;
    private const double CellSizeDegrees = 0.01;
    private readonly IReadOnlyList<NetworkHeatmapCell> _cells;
    private readonly Dictionary<(int X, int Y), NetworkHeatmapCell> _index;
    private readonly double _maxDamageDensity;
    private readonly double _maxNetworkContext;

    public IReadOnlyList<NetworkHeatmapCell> Cells => _cells;

    public HistoricalDamagePredictionService(string? dataFile = null)
    {
        dataFile ??= Path.Combine(AppContext.BaseDirectory, "Prediction", "network_heatmap.csv");
        _cells = File.Exists(dataFile) ? LoadCells(dataFile) : Array.Empty<NetworkHeatmapCell>();
        _index = _cells.ToDictionary(c => CellKey(c.Longitude, c.Latitude));
        (_maxDamageDensity, _maxNetworkContext) = FindSignalMaxima();
    }

    public OutagePrediction Predict(OutagePredictionRequest request)
    {
        if (_cells.Count == 0)
            return OutagePrediction.NoData("The aggregated GIS layer could not be loaded. This location cannot be predicted.", request.MedicalEquipmentNeed);
        if (request.Location.Longitude is < -180 or > 180 || request.Location.Latitude is < -90 or > 90)
            return OutagePrediction.NoData("The selected coordinates are outside the valid map range.", request.MedicalEquipmentNeed);

        var nearby = GetNearby(request.Location);
        if (nearby.Count == 0)
            return OutagePrediction.NoData("No nearby premise, conductor, pole, transformer, sectionalizing device, or historical damage data was found within 2 km. This location is outside the supported GIS coverage.", request.MedicalEquipmentNeed);

        double Sum(Func<NetworkHeatmapCell, double> selector) => nearby.Sum(item => selector(item.Cell) * item.Weight);
        var damageDensity = Sum(c => c.DamageScore);
        var networkContext = Sum(NetworkContextSignal);
        var damageSignal = _maxDamageDensity > 0 ? Math.Clamp(damageDensity / _maxDamageDensity, 0, 1) : 0;
        var networkSignal = _maxNetworkContext > 0 ? Math.Clamp(networkContext / _maxNetworkContext, 0, 1) : 0;
        var baseIndex = 100 * (0.75 * damageSignal + 0.25 * networkSignal);

        var premises = Sum(c => c.Premises);
        var overheadPremises = Sum(c => c.OverheadPremises);
        var primaryKm = Sum(c => c.PrimaryKilometers);
        var primaryOverheadKm = Sum(c => c.PrimaryOverheadKilometers);
        var secondaryKm = Sum(c => c.SecondaryKilometers);
        var secondaryOverheadKm = Sum(c => c.SecondaryOverheadKilometers);
        var poles = Sum(c => c.Poles);
        var transformers = Sum(c => c.Transformers);
        var fuses = Sum(c => c.Fuses);
        var switches = Sum(c => c.Switches);
        var reclosers = Sum(c => c.Reclosers);
        var transformerLinks = Sum(c => c.TransformerPremiseLinks);
        var deviceLinks = Sum(c => c.DevicePremiseLinks);
        var damageReports = Sum(c => c.DamageReports);
        var overheadLength = primaryOverheadKm + secondaryOverheadKm;
        var totalConductorLength = primaryKm + secondaryKm;
        var overheadShare = totalConductorLength > 0.01
            ? Math.Clamp(overheadLength / totalConductorLength, 0, 1)
            : (premises > 0 ? Math.Clamp(overheadPremises / premises, 0, 1) : 0.5);
        var exposureMultiplier = request.Exposure.OverrideMultiplier ?? (0.8 + 0.4 * overheadShare);
        var adjusted = Math.Clamp(baseIndex * request.Storm.Multiplier * exposureMultiplier, 0, 100);
        var band = adjusted switch { < 34 => "Lower", < 67 => "Elevated", _ => "Higher" };
        var localSupport = Math.Clamp(0.45 + Math.Min(damageReports, 12) * 0.025 + Math.Min(premises, 200) / 2000, 0.45, 0.90);
        var count = (int)Math.Round(damageReports);
        var networkSummary = $"Within about 2 km (distance-weighted): {premises:0} premises; {primaryKm:0.0} km primary and {secondaryKm:0.0} km secondary conductors; {poles:0} poles; {transformers:0} transformers; {fuses + switches + reclosers:0} sectionalizing devices. Approx. overhead share: {overheadShare:P0}. Topology links: {transformerLinks:0} premise-to-transformer and {deviceLinks:0} premise-to-device paths nearby (upstream device counts can overlap).";
        var exposureText = request.Exposure.OverrideMultiplier is null
            ? $"GIS-measured overhead share {overheadShare:P0} gives ×{exposureMultiplier:0.00}."
            : $"Selected asset exposure scenario gives ×{exposureMultiplier:0.00}.";
        var explanation = $"The local baseline combines a distance-weighted ARCOS damage signal (75%) and nearby network/premise density (25%). Storm scenario {request.Storm.Name.ToLowerInvariant()} applies ×{request.Storm.Multiplier:0.00}; {exposureText} Result: {adjusted:0}/100 ({band.ToLowerInvariant()}). Damage reports are historical; no unaffected-asset or storm-specific denominator is supplied, so this is not a calibrated outage probability or utility forecast.";
        var directHours = Sum(c => c.DirectLaborHours) + damageReports * request.Access.DirectHoursAdderPerJob;
        var totalPersonHours = directHours * IndirectLaborMultiplier;
        var crewDays = request.CrewSize > 0 ? totalPersonHours / (request.CrewSize * 8.0) : 0;
        var restorationRange = damageReports < 0.25 || directHours <= 0
            ? "Not enough nearby damage records to size a repair scenario"
            : $"{Math.Max(0.1, crewDays * 0.75):0.0}–{Math.Max(0.2, crewDays * 1.35):0.0} crew workdays ({request.CrewSize}-person crew, 8-hour days)";
        var restorationText = damageReports < 0.25
            ? "There are not enough nearby historical repair records for a labor scenario."
            : $"Nearby historical damage mix: about {damageReports:0} weighted reports and {directHours:0.0} direct crew-hours. Applying the supplied ×{IndirectLaborMultiplier:0.0} indirect labor multiplier gives {totalPersonHours:0} person-hours; the duration range uses a {request.CrewSize}-person crew and an 8-hour workday. This is a repeat-pattern scenario, not an active outage prediction.";
        var supportText = request.MedicalEquipmentNeed || request.AccessibilitySupportNeed || request.CriticalFacility
            ? " Declared support needs guide preparation advice only; they do not change outage risk."
            : string.Empty;
        var accessText = request.Access.DirectHoursAdderPerJob > 0
            ? $" The {request.Access.Name.ToLowerInvariant()} assumption adds {request.Access.DirectHoursAdderPerJob:0.###} direct labor hours per reported job."
            : string.Empty;
        explanation += accessText + " " + restorationText + supportText + " Nearby fuses, switches, and reclosers indicate possible sectionalizing points; exact switching sequence and customer-by-customer step restoration are not simulated.";
        var guidance = request.MedicalEquipmentNeed
            ? "Keep essential medical equipment charged, confirm backup-power arrangements with your care team, and identify a support contact. If someone is in immediate danger, call emergency services."
            : request.AccessibilitySupportNeed || request.CriticalFacility
                ? "Review your continuity plan, charge essential devices, and identify an accessible backup location and support contact."
                : "Charge essential devices, keep flashlights and water available, and review your household outage plan.";
        return new OutagePrediction(true, band, adjusted, baseIndex, localSupport, request.Storm.Multiplier, exposureMultiplier, overheadShare, count, networkSummary, restorationRange, totalPersonHours, explanation, guidance);
    }

    public double? GetLocationBaselineIndex(MapCoordinate location)
    {
        var nearby = GetNearby(location);
        if (nearby.Count == 0) return null;
        var damage = nearby.Sum(x => x.Cell.DamageScore * x.Weight);
        var network = nearby.Sum(x => NetworkContextSignal(x.Cell) * x.Weight);
        var damageSignal = _maxDamageDensity > 0 ? Math.Clamp(damage / _maxDamageDensity, 0, 1) : 0;
        var networkSignal = _maxNetworkContext > 0 ? Math.Clamp(network / _maxNetworkContext, 0, 1) : 0;
        return 100 * (0.75 * damageSignal + 0.25 * networkSignal);
    }

    public string GetNetworkSummary(MapCoordinate location)
    {
        var nearby = GetNearby(location);
        if (nearby.Count == 0) return "No GIS assets or historical damage in the supported area.";
        double Sum(Func<NetworkHeatmapCell, double> selector) => nearby.Sum(x => selector(x.Cell) * x.Weight);
        var premises = Sum(c => c.Premises); var overheadPremises = Sum(c => c.OverheadPremises);
        var primary = Sum(c => c.PrimaryKilometers); var primaryOverhead = Sum(c => c.PrimaryOverheadKilometers);
        var secondary = Sum(c => c.SecondaryKilometers); var secondaryOverhead = Sum(c => c.SecondaryOverheadKilometers);
        var totalLength = primary + secondary;
        var overheadShare = totalLength > 0.01 ? (primaryOverhead + secondaryOverhead) / totalLength : (premises > 0 ? overheadPremises / premises : 0.5);
        var transformerLinks = Sum(c => c.TransformerPremiseLinks); var deviceLinks = Sum(c => c.DevicePremiseLinks);
        return $"Within ~2 km (distance weighted): {premises:0} premises; {primary:0.0} km primary and {secondary:0.0} km secondary conductors; {Sum(c => c.Poles):0} poles; {Sum(c => c.Transformers):0} transformers; {Sum(c => c.SectionalizingDevices):0} fuses/switches/reclosers. Overhead share {overheadShare:P0}. Approx. upstream links: {transformerLinks:0} to transformers and {deviceLinks:0} to sectionalizing devices; nested device paths can overlap.";
    }

    private List<(NetworkHeatmapCell Cell, double Weight)> GetNearby(MapCoordinate location)
    {
        var latDelta = SearchRadiusKilometers / 110.9;
        var lonDelta = SearchRadiusKilometers / (111.0 * Math.Max(0.1, Math.Cos(location.Latitude * Math.PI / 180)));
        var minX = (int)Math.Floor((location.Longitude - lonDelta) / CellSizeDegrees);
        var maxX = (int)Math.Floor((location.Longitude + lonDelta) / CellSizeDegrees);
        var minY = (int)Math.Floor((location.Latitude - latDelta) / CellSizeDegrees);
        var maxY = (int)Math.Floor((location.Latitude + latDelta) / CellSizeDegrees);
        var found = new List<(NetworkHeatmapCell, double)>();
        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        {
            if (!_index.TryGetValue((x, y), out var cell)) continue;
            var distance = DistanceKm(location, new MapCoordinate(cell.Longitude, cell.Latitude));
            if (distance <= SearchRadiusKilometers)
                found.Add((cell, Math.Exp(-0.5 * Math.Pow(distance / KernelBandwidthKilometers, 2))));
        }
        return found;
    }

    private (double Damage, double Network) FindSignalMaxima()
    {
        var maxDamage = 0d; var maxNetwork = 0d;
        foreach (var cell in _cells)
        {
            var local = GetNearby(new MapCoordinate(cell.Longitude, cell.Latitude));
            maxDamage = Math.Max(maxDamage, local.Sum(x => x.Cell.DamageScore * x.Weight));
            maxNetwork = Math.Max(maxNetwork, local.Sum(x => NetworkContextSignal(x.Cell) * x.Weight));
        }
        return (maxDamage, maxNetwork);
    }

    private static double NetworkContextSignal(NetworkHeatmapCell cell) =>
        0.50 * Math.Log(1 + cell.Premises) +
        0.30 * Math.Log(1 + 10 * (cell.PrimaryKilometers + cell.SecondaryKilometers)) +
        0.20 * Math.Log(1 + cell.Poles + cell.Transformers);

    private static IReadOnlyList<NetworkHeatmapCell> LoadCells(string path)
    {
        var cells = new List<NetworkHeatmapCell>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 28 || !double.TryParse(p[0], CultureInfo.InvariantCulture, out var lon) || !double.TryParse(p[1], CultureInfo.InvariantCulture, out var lat)) continue;
            double V(int index) => double.TryParse(p[index], CultureInfo.InvariantCulture, out var value) ? value : 0;
            cells.Add(new NetworkHeatmapCell(lon, lat,
                V(2),V(3),V(4),V(5),V(6),V(7),V(8),V(9),V(10),V(11),V(12),V(13),V(14),V(15),V(16),V(17),V(18),V(19),V(20),V(21),V(22),V(23),V(24),V(25),V(26),V(27)));
        }
        return cells;
    }

    public static (int X, int Y) CellKey(double longitude, double latitude) =>
        ((int)Math.Floor(longitude / CellSizeDegrees), (int)Math.Floor(latitude / CellSizeDegrees));

    private static double DistanceKm(MapCoordinate a, MapCoordinate b)
    {
        const double earthRadiusKm = 6371;
        var lat1 = a.Latitude * Math.PI / 180; var lat2 = b.Latitude * Math.PI / 180;
        var dLat = lat2 - lat1; var dLon = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2), 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }
}
