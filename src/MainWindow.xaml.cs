using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AI_Outage_Concierge.Assistant;
using AI_Outage_Concierge.Prediction;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Nts;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;

namespace AI_Outage_Concierge;

public partial class MainWindow : Window
{
    private const double GridCellHalfSize = 0.005;
    private const double WebMercatorRadius = 6378137;
    private readonly HistoricalDamagePredictionService _predictionService = new();
    private readonly IConciergeChatService _chatService = new StubConciergeChatService();
    private MapCoordinate? _selectedCoordinate;
    private OutagePrediction? _currentPrediction;
    private Mapsui.Map? _map;
    private MRect? _coverageExtent;
    private MemoryLayer? _coverageLayer;
    private MemoryLayer? _premisesLayer;
    private MemoryLayer? _conductorLayer;
    private MemoryLayer? _polesLayer;
    private MemoryLayer? _transformerLayer;
    private MemoryLayer? _protectionLayer;
    private MemoryLayer? _selectionLayer;
    private double _fitResolution;
    private bool _mapReady;
    private bool _hasRequestedEstimate;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeAreaMap();
    }

    private void InitializeAreaMap()
    {
        if (_predictionService.Cells.Count == 0)
        {
            CoordinateText.Text = "No historical coverage data is available.";
            return;
        }

        var minLongitude = _predictionService.Cells.Min(cell => cell.Longitude) - GridCellHalfSize;
        var maxLongitude = _predictionService.Cells.Max(cell => cell.Longitude) + GridCellHalfSize;
        var minLatitude = _predictionService.Cells.Min(cell => cell.Latitude) - GridCellHalfSize;
        var maxLatitude = _predictionService.Cells.Max(cell => cell.Latitude) + GridCellHalfSize;
        var southWest = Project(minLongitude, minLatitude);
        var northEast = Project(maxLongitude, maxLatitude);
        _coverageExtent = new MRect(southWest.X, southWest.Y, northEast.X, northEast.Y);

        _coverageLayer = new MemoryLayer("Historical damage coverage")
        {
            Features = BuildCoverageFeatures(),
            Enabled = false,
            Opacity = 0.78
        };
        _premisesLayer = BuildPointLayer("Premises", cell => cell.Premises > 0, cell => cell.Premises, "#39434A");
        _conductorLayer = BuildPointLayer("Conductors", cell => cell.PrimaryKilometers + cell.SecondaryKilometers > 0, cell => cell.PrimaryKilometers + cell.SecondaryKilometers, "#276B83");
        _polesLayer = BuildPointLayer("Poles", cell => cell.Poles > 0, cell => cell.Poles, "#535D63");
        _transformerLayer = BuildPointLayer("Transformers", cell => cell.Transformers > 0, cell => cell.Transformers, "#3E688C");
        _protectionLayer = BuildPointLayer("Protection devices", cell => cell.SectionalizingDevices > 0, cell => cell.SectionalizingDevices, "#6F5091");
        _selectionLayer = new MemoryLayer("Selected location") { Opacity = 0.88 };

        _map = new Mapsui.Map();
        while (_map.Widgets.TryDequeue(out _)) { }
        _map.Layers.Add(OpenStreetMap.CreateTileLayer("AIOutageConcierge/1.0"));
        _map.Layers.Add(_coverageLayer);
        _map.Layers.Add(_premisesLayer);
        _map.Layers.Add(_conductorLayer);
        _map.Layers.Add(_polesLayer);
        _map.Layers.Add(_transformerLayer);
        _map.Layers.Add(_protectionLayer);
        _map.Layers.Add(_selectionLayer);
        _map.Navigator.OverridePanBounds = _coverageExtent;
        _map.Navigator.Limiter = new ViewportLimiterKeepWithinExtent();
        AreaMapControl.Map = _map;
        AreaMapControl.SizeChanged += AreaMapControl_SizeChanged;

        CoordinateText.Text = $"Historical coverage: {_predictionService.Cells.Count:N0} populated grid cells · southwest Virginia";
        _mapReady = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitCoverage));
    }

    private IEnumerable<IFeature> BuildCoverageFeatures()
    {
        var features = new List<IFeature>();
        foreach (var cell in _predictionService.Cells)
        {
            if (cell.DamageScore <= 0 && cell.Premises <= 0 && cell.PrimaryKilometers <= 0 && cell.Poles <= 0) continue;
            var ring = new[]
            {
                Project(cell.Longitude - GridCellHalfSize, cell.Latitude - GridCellHalfSize),
                Project(cell.Longitude + GridCellHalfSize, cell.Latitude - GridCellHalfSize),
                Project(cell.Longitude + GridCellHalfSize, cell.Latitude + GridCellHalfSize),
                Project(cell.Longitude - GridCellHalfSize, cell.Latitude + GridCellHalfSize),
                Project(cell.Longitude - GridCellHalfSize, cell.Latitude - GridCellHalfSize)
            }.Select(point => new Coordinate(point.X, point.Y)).ToArray();
            var polygon = new Polygon(new LinearRing(ring));
            var index = _predictionService.GetLocationBaselineIndex(new MapCoordinate(cell.Longitude, cell.Latitude)) ?? 0;
            var color = index >= 67 ? new Mapsui.Styles.Color(176, 52, 37, 110)
                : index >= 34 ? new Mapsui.Styles.Color(211, 124, 52, 100)
                : new Mapsui.Styles.Color(224, 178, 104, 88);
            var feature = new GeometryFeature(polygon);
            feature.Styles.Add(new VectorStyle
            {
                Fill = new Mapsui.Styles.Brush(color),
                Outline = new Mapsui.Styles.Pen(new Mapsui.Styles.Color(255, 255, 255, 75), 0.3)
            });
            features.Add(feature);
        }
        return features;
    }

    private MemoryLayer BuildPointLayer(string name, Func<NetworkHeatmapCell, bool> include, Func<NetworkHeatmapCell, double> value, string colorHex)
    {
        var baseColor = Mapsui.Styles.Color.FromString(colorHex);
        var color = new Mapsui.Styles.Color(baseColor.R, baseColor.G, baseColor.B, 220);
        var features = new List<IFeature>();
        foreach (var cell in _predictionService.Cells.Where(include))
        {
            var point = Project(cell.Longitude, cell.Latitude);
            var feature = new GeometryFeature(new NetTopologySuite.Geometries.Point(point.X, point.Y));
            feature.Styles.Add(new SymbolStyle
            {
                Fill = new Mapsui.Styles.Brush(color),
                SymbolScale = Math.Clamp(0.16 + Math.Log(1 + value(cell)) * 0.025, 0.16, 0.3)
            });
            features.Add(feature);
        }
        return new MemoryLayer(name) { Features = features, Enabled = false, Opacity = 0.82 };
    }

    private void AreaMapControl_MapTapped(object? sender, Mapsui.MapEventArgs e)
    {
        if (e.GestureType != Mapsui.Manipulations.GestureType.SingleTap) return;
        _selectedCoordinate = Unproject(e.WorldPosition.X, e.WorldPosition.Y);
        if (_selectionLayer is not null)
        {
            var location = Project(_selectedCoordinate.Longitude, _selectedCoordinate.Latitude);
            var marker = new GeometryFeature(new NetTopologySuite.Geometries.Point(location.X, location.Y));
            marker.Styles.Add(new SymbolStyle
            {
                Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(23, 25, 28, 230)),
                SymbolScale = 0.42
            });
            _selectionLayer.Features = [marker];
            _map?.Refresh();
        }
        CoordinateText.Text = $"Selected: {FormatCoordinate(_selectedCoordinate)}";
        NetworkText.Text = _predictionService.GetNetworkSummary(_selectedCoordinate);
        if (_hasRequestedEstimate) UpdatePrediction();
    }

    private void AreaMapControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_map is null || _coverageExtent is null || AreaMapControl.ActualWidth < 1 || AreaMapControl.ActualHeight < 1) return;
        UpdateZoomBounds();
        if (_fitResolution <= 0) FitCoverage();
    }

    private void UpdateZoomBounds()
    {
        if (_map is null || _coverageExtent is null || AreaMapControl.ActualWidth < 1 || AreaMapControl.ActualHeight < 1) return;
        _fitResolution = Math.Max(_coverageExtent.Width / AreaMapControl.ActualWidth, _coverageExtent.Height / AreaMapControl.ActualHeight);
        _map.Navigator.OverrideZoomBounds = new MMinMax(_fitResolution / 80, _fitResolution);
    }

    private void FitCoverage()
    {
        if (_map is null || _coverageExtent is null || AreaMapControl.ActualWidth < 1 || AreaMapControl.ActualHeight < 1) return;
        UpdateZoomBounds();
        _map.Navigator.ZoomToBox(_coverageExtent, MBoxFit.Fit);
    }

    private void CoverageView_Click(object sender, RoutedEventArgs e) => FitCoverage();

    private void Layer_Changed(object sender, RoutedEventArgs e)
    {
        if (!_mapReady) return;
        if (_coverageLayer is not null) _coverageLayer.Enabled = CoverageLayerCheck.IsChecked == true;
        if (_premisesLayer is not null) _premisesLayer.Enabled = PremisesLayerCheck.IsChecked == true;
        if (_conductorLayer is not null) _conductorLayer.Enabled = ConductorLayerCheck.IsChecked == true;
        if (_polesLayer is not null) _polesLayer.Enabled = PolesLayerCheck.IsChecked == true;
        if (_transformerLayer is not null) _transformerLayer.Enabled = TransformerLayerCheck.IsChecked == true;
        if (_protectionLayer is not null) _protectionLayer.Enabled = ProtectionLayerCheck.IsChecked == true;
        _map?.Refresh();
    }

    private static MPoint Project(double longitude, double latitude)
    {
        var x = WebMercatorRadius * longitude * Math.PI / 180;
        var clampedLatitude = Math.Clamp(latitude, -85.05112878, 85.05112878);
        var y = WebMercatorRadius * Math.Log(Math.Tan(Math.PI / 4 + clampedLatitude * Math.PI / 360));
        return new MPoint(x, y);
    }

    private static MapCoordinate Unproject(double x, double y)
    {
        var longitude = x / WebMercatorRadius * 180 / Math.PI;
        var latitude = (2 * Math.Atan(Math.Exp(y / WebMercatorRadius)) - Math.PI / 2) * 180 / Math.PI;
        return new MapCoordinate(longitude, latitude);
    }

    private void PredictButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCoordinate is null)
        {
            RiskValue.Text = "— / 100"; RiskBandValue.Text = "Select location";
            RiskConfidence.Text = "Click a point on the map first.";
            FactorsText.Text = "No location has been selected, so no estimate is available.";
            RestorationValue.Text = "Not available";
            RestorationDetailsText.Text = "Select a location to build a repair-effort scenario from nearby historical damage.";
            return;
        }
        _hasRequestedEstimate = true;
        UpdatePrediction();
    }

    private void Scenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mapReady && _hasRequestedEstimate && IsLoaded) UpdatePrediction();
    }

    private void UpdatePrediction()
    {
        if (_selectedCoordinate is null) return;
        var storm = SelectedStorm();
        var exposure = SelectedExposure();
        _currentPrediction = _predictionService.Predict(new OutagePredictionRequest(
            _selectedCoordinate, storm, exposure, SelectedCrewSize(), SelectedAccess(),
            MedicalCheck.IsChecked == true, AccessCheck.IsChecked == true, CriticalCheck.IsChecked == true));
        if (!_currentPrediction.HasData)
        {
            RiskValue.Text = "N/A"; RiskBandValue.Text = "No local data";
            RiskConfidence.Text = "This area is outside the supported damage data.";
            FactorsText.Text = _currentPrediction.Explanation;
            RestorationValue.Text = "Not available";
            RestorationDetailsText.Text = _currentPrediction.Explanation;
        }
        else
        {
            RiskValue.Text = $"{_currentPrediction.RelativeRiskIndex:0} / 100";
            RiskBandValue.Text = _currentPrediction.RiskBand;
            RiskConfidence.Text = $"Nearby reports: {_currentPrediction.NearbyDamageReports} · local support (heuristic): {_currentPrediction.LocalDataSupport:P0}";
            FactorsText.Text = $"Location baseline {_currentPrediction.BaseRiskIndex:0}/100 × storm {_currentPrediction.StormMultiplier:0.00} × exposure {_currentPrediction.ExposureMultiplier:0.00} = {_currentPrediction.RelativeRiskIndex:0}/100. {_currentPrediction.Explanation}";
            RestorationValue.Text = _currentPrediction.RestorationRange;
            RestorationDetailsText.Text = _currentPrediction.TotalPersonHours is null
                ? _currentPrediction.Explanation
                : $"About {_currentPrediction.TotalPersonHours:0} total person-hours after the labor multiplier. Based on the historical local damage mix, selected crew, 8-hour days, and an illustrative −25%/+35% duration buffer. Not a live outage estimate.";
        }
        NetworkText.Text = _currentPrediction.NetworkSummary;
        GuidanceText.Text = _currentPrediction.PreparationGuidance;
        EligibilityText.Text = SmsCheck.IsChecked == true || EmailCheck.IsChecked == true
            ? "Preference selected for prototype alerts. No alert channel is connected."
            : "No alert channel selected. Enable SMS or email above to indicate a preference.";
        ChatResponse.Text = _currentPrediction.HasData
            ? "I can explain the historical damage index and your scenario assumptions. This assistant is a prototype without live outage information."
            : "This location is outside the historical dataset coverage, so I can’t provide a data-backed local estimate. Check official utility updates.";
    }

    private StormScenario SelectedStorm()
    {
        if (StormCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
            return new StormScenario("Typical severity", 1);
        var label = item.Content?.ToString()?.Split('·')[0].Trim() ?? "Typical severity";
        return new StormScenario(label, multiplier);
    }

    private AssetExposure SelectedExposure()
    {
        if (ExposureCombo.SelectedItem is not ComboBoxItem item || string.Equals(item.Tag?.ToString(), "auto", StringComparison.OrdinalIgnoreCase))
            return new AssetExposure("GIS-measured local mix", null);
        if (!double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
            return new AssetExposure("GIS-measured local mix", null);
        var label = item.Content?.ToString()?.Split('·')[0].Trim() ?? "Typical overhead assets";
        return new AssetExposure(label, multiplier);
    }

    private int SelectedCrewSize() => CrewCombo.SelectedItem is ComboBoxItem item &&
        int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var crewSize) ? crewSize : 4;

    private RepairAccessCondition SelectedAccess()
    {
        if (AccessCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var extraHours))
            return new RepairAccessCondition("Standard access", 0);
        var label = item.Content?.ToString()?.Split('·')[0].Trim() ?? "Standard access";
        return new RepairAccessCondition(label, extraHours);
    }

    private void EditButton_Click(object sender, RoutedEventArgs e) { ContentScroller.ScrollToTop(); StormCombo.Focus(); }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var question = ChatInput.Text.Trim();
        if (question.Length == 0) return;
        ChatResponse.Text = _chatService.GetResponse(question, _currentPrediction);
        ChatInput.Clear();
    }

    private static double NormalizeLongitude(double longitude)
    {
        while (longitude > 180) longitude -= 360;
        while (longitude < -180) longitude += 360;
        return longitude;
    }

    private static string FormatCoordinate(MapCoordinate coordinate)
    {
        var latitudeHemisphere = coordinate.Latitude < 0 ? "S" : "N";
        var longitudeHemisphere = coordinate.Longitude < 0 ? "W" : "E";
        return $"{Math.Abs(coordinate.Latitude):0.00000}° {latitudeHemisphere}, {Math.Abs(coordinate.Longitude):0.00000}° {longitudeHemisphere}";
    }
}
