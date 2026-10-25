using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using larp.Assistant;
using larp.Prediction;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Nts;
using Mapsui.Providers;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;

namespace larp;

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

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeAreaMap();
        RefreshPreparationAndAlerts();
    }

    private void WindowChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsWindowControl(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
            return;
        }

        DragMove();
    }

    private static bool IsWindowControl(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        FullScreenButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        FullScreenButton.ToolTip = WindowState == WindowState.Maximized ? "Exit full screen" : "Enter full screen";
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

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
        MarkEstimatePending();
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
            NetworkText.Text = "Select a location, then view your estimate to refresh nearby network details.";
            _currentPrediction = null;
            PreparationLevelText.Text = "Select a location";
            GuidanceText.Text = "Choose a point on the map, then view an estimate for guidance tailored to its historical index.";
            RefreshPreparationAndAlerts();
            return;
        }
        UpdatePrediction();
    }

    private void Scenario_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mapReady && IsLoaded) MarkEstimatePending();
    }

    private void Needs_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapReady && IsLoaded) MarkEstimatePending();
    }

    private void AlertPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (_mapReady && IsLoaded) MarkEstimatePending();
    }

    private void MarkEstimatePending()
    {
        _currentPrediction = null;
        RiskValue.Text = "— / 100";
        RiskBandValue.Text = "Estimate pending";
        RiskConfidence.Text = "Inputs changed. Press View my estimate to refresh.";
        RestorationValue.Text = "Estimate pending";
        RestorationDetailsText.Text = "Press View my estimate to refresh the repair scenario.";
        FactorsText.Text = "Press View my estimate to refresh the risk index and scenario factors.";
        NetworkText.Text = "Press View my estimate to refresh nearby network details.";
        PreparationLevelText.Text = "Estimate pending";
        GuidanceText.Text = "Press View my estimate to refresh guidance for the selected location, scenario, and declared needs.";
        EligibilityText.Text = "Press View my estimate to refresh alert eligibility for the current preferences.";
        AlertPlanPanel.Visibility = Visibility.Collapsed;
        ChatResponse.Text = "Your estimate inputs changed. Press View my estimate to refresh the information before asking about it.";
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
        RefreshPreparationAndAlerts();
        ChatResponse.Text = _currentPrediction.HasData
            ? "I can explain the historical damage index and your scenario assumptions. This assistant is a prototype without live outage information."
            : "This location is outside the historical dataset coverage, so I can’t provide a data-backed local estimate. Check official utility updates.";
    }

    private void RefreshPreparationAndAlerts()
    {
        var prediction = _currentPrediction;
        var medicalNeed = MedicalCheck.IsChecked == true;
        var accessNeed = AccessCheck.IsChecked == true;
        var criticalNeed = CriticalCheck.IsChecked == true;
        var declaredNeeds = new List<string>();
        if (medicalNeed) declaredNeeds.Add("Because you selected a powered medical-equipment need, confirm backup runtime and a plan with your care provider or equipment supplier so you know what to do before device power runs low.");
        if (accessNeed) declaredNeeds.Add("Because you selected an assistance need, arrange an accessible check-in and backup transport or support contact to reduce barriers if normal services are disrupted.");
        if (criticalNeed) declaredNeeds.Add("Because you identified a critical facility, review continuity, backup power, staffing, and service-transfer triggers so essential services can continue if power is interrupted.");

        if (prediction is null)
        {
            PreparationLevelText.Text = "Risk-based guidance · awaiting estimate";
            GuidanceText.Text = "Select a location and view an estimate for advice based on its historical damage index. Until then, keep phones charged and basic supplies together so you can communicate and respond if service is interrupted; follow official weather and utility updates.";
        }
        else if (!prediction.HasData)
        {
            PreparationLevelText.Text = "Local index unavailable";
            GuidanceText.Text = "This location has no supporting historical data, so the app cannot compare local damage patterns or size a repair scenario. That is not evidence of low risk; use official weather and utility advisories to plan.";
        }
        else
        {
            var score = prediction.RelativeRiskIndex;
            var severity = score switch { < 25 => "Lower", < 50 => "Moderate", < 75 => "Elevated", _ => "High" };
            PreparationLevelText.Text = $"{severity} historical index · {score:0}/100";
            var riskAdvice = score switch
            {
                < 25 => "The historical index is lower here, but it cannot rule out an outage. Keep phones charged and basic supplies together so you can communicate and respond if service is interrupted.",
                < 50 => "This moderate index reflects a stronger historical damage signal than lower-index areas, not a guaranteed outage. Charge phones and backup batteries so you can stay in contact; stage lighting and water so you are not searching for essentials if power stops.",
                < 75 => "This elevated index reflects more historical damage signal in the nearby data, not certainty of an outage. Charge backup power to preserve communication, stage several days of essentials to reduce last-minute trips, and confirm a check-in contact in case normal services are disrupted.",
                _ => "This high index reflects the strongest historical damage signal band, not a prediction that an outage will occur. Confirm backup power and a support contact now, and stage essentials so you can act if travel, charging, or supply access becomes difficult."
            };
            var repairGuidance = prediction.TotalPersonHours is double personHours && SelectedCrewSize() > 0
                ? BuildRepairDurationGuidance(personHours, SelectedCrewSize(), prediction.RestorationRange)
                : "Nearby repair duration is not available; this index is not a customer outage-duration estimate.";
            GuidanceText.Text = $"{riskAdvice} {repairGuidance} Declared needs affect preparation advice only; they do not change the risk index.";
        }

        if (declaredNeeds.Count > 0)
            GuidanceText.Text += " " + string.Join(" ", declaredNeeds);

        var channels = GetSelectedAlertChannels();
        if (channels.Count == 0)
        {
            EligibilityText.Text = "Alerts are off. Opt in to SMS or email to see a prototype alert plan.";
            AlertPlanPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AlertPlanPanel.Visibility = Visibility.Visible;
        var channelText = string.Join(" and ", channels);
        if (prediction is null)
        {
            EligibilityText.Text = $"Opted in to {channelText}. Calculate an estimate to check pre-storm eligibility.";
        }
        else if (!prediction.HasData)
        {
            EligibilityText.Text = $"Opted in to {channelText}, but this location has no supporting data for a risk-based pre-storm alert.";
        }
        else if (prediction.RelativeRiskIndex >= 50)
        {
            EligibilityText.Text = $"Eligible for a prototype pre-storm advisory on {channelText}: the historical index is at least 50/100 and you opted in.";
        }
        else
        {
            EligibilityText.Text = $"Opted in to {channelText}. The historical index is below the prototype pre-storm threshold of 50/100, so no risk-based pre-storm advisory qualifies.";
        }

        var optedNeeds = new List<string>();
        if (medicalNeed) optedNeeds.Add("powered medical equipment");
        if (accessNeed) optedNeeds.Add("assistance");
        if (criticalNeed) optedNeeds.Add("critical facility");
        var escalation = optedNeeds.Count == 0
            ? "Escalation: recommend a human check-in after a utility-confirmed outage; direct immediate danger to emergency services."
            : $"Escalation: a human check-in is recommended after a confirmed outage because you declared {string.Join(", ", optedNeeds)}. No need is inferred. Immediate danger requires emergency services.";
        AlertPlanText.Text = $"Prototype rules: explicit opt-in is required; only selected channels ({channelText}) are used. A supported index of 50/100 or higher qualifies for a pre-storm advisory. Suggested timing: about 24 hours before a forecast storm, giving time to prepare; send follow-ups only after a 10+ point index change or a band crossing to avoid messages for small shifts. SMS suits short urgent notices because they are quick to read; email suits fuller checklists that customers may want to revisit. Outage confirmation, utility estimate changes, and restoration-complete alerts require official utility events, which are not connected. {escalation} No messages are sent by this prototype.";

        PreStormMessageText.Text = prediction is { HasData: true }
            ? BuildPreStormMessage(prediction)
            : "Storm planning update: a local historical risk estimate is unavailable for your selected area. Prepare using official weather and utility advisories; no local index is available.";
        OutageConfirmedMessageText.Text = "The utility has confirmed an outage in your area. Restoration timing is not available in this prototype. Check official utility updates. If you see a downed line, stay away and call emergency services and your utility.";
        EstimateUpdatedMessageText.Text = "Update: the utility revised its restoration estimate to [new utility-reported time]. Check the latest utility update; this message is sent only after an official estimate change.";
        RestorationCompleteMessageText.Text = "The utility reports that service has been restored in your area. If your home is still without power, report it to the utility. This message requires an official restoration event.";
    }

    private static string BuildRepairDurationGuidance(double totalPersonHours, int crewSize, string restorationRange)
    {
        if (restorationRange.StartsWith("Not enough", StringComparison.OrdinalIgnoreCase))
            return "Nearby historical repair records are too sparse to estimate crew workdays, so the app cannot tailor preparation to a repair duration.";
        var crewDays = totalPersonHours / (crewSize * 8.0);
        var upperDays = Math.Max(0.2, crewDays * 1.35);
        var durationAdvice = upperDays >= 3
            ? "The upper repair scenario spans several crew workdays. Because an extended disruption can make supplies and charging harder to access, stage items you may need to replenish."
            : upperDays > 1
                ? "The upper repair scenario extends beyond one crew workday. Keep supplies and check-ins planned in case the disruption continues across shifts."
                : "This scenario is under about one crew workday, but that measures repair workload, not when your power returns. Keep essentials ready until the utility confirms service is restored.";
        return $"Repair-work scenario: {restorationRange}. {durationAdvice} This is an estimate of crew effort from historical reports, not a customer outage-duration estimate.";
    }

    private static string BuildPreStormMessage(OutagePrediction prediction)
    {
        var preparationReason = prediction.RelativeRiskIndex switch
        {
            < 25 => "A lower index cannot rule out an outage, so keep a charged phone ready for official updates.",
            < 50 => "This moderate historical signal is a reason to charge devices and stage lighting and water, so essentials are ready if service is interrupted.",
            < 75 => "This elevated historical signal supports charging backup power and staging supplies, so you can stay in contact and avoid last-minute trips if service is interrupted.",
            _ => "This high historical signal supports confirming backup power and a support contact now, so you have a plan if service or travel is disrupted."
        };
        return $"Storm planning update: historical damage index {prediction.RelativeRiskIndex:0}/100 ({prediction.RiskBand}). {preparationReason} Scenario repair effort: {prediction.RestorationRange}; crew-work estimate only, not an outage ETA. Follow official updates.";
    }

    private List<string> GetSelectedAlertChannels()
    {
        var channels = new List<string>();
        if (SmsCheck.IsChecked == true) channels.Add("SMS");
        if (EmailCheck.IsChecked == true) channels.Add("email");
        return channels;
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
