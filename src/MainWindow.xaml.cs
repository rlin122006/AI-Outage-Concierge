using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AI_Outage_Concierge.Assistant;
using AI_Outage_Concierge.Prediction;

namespace AI_Outage_Concierge;

public partial class MainWindow : Window
{
    private readonly IOutagePredictionService _predictionService = new StubOutagePredictionService();
    private readonly IConciergeChatService _chatService = new StubConciergeChatService();
    private MapCoordinate? _selectedCoordinate;
    private OutagePrediction? _currentPrediction;

    public MainWindow() => InitializeComponent();

    private void MapCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(MapCanvas);
        _selectedCoordinate = new MapCoordinate(Math.Round(point.X / MapCanvas.ActualWidth * 100, 1), Math.Round(point.Y / MapCanvas.ActualHeight * 100, 1));
        Canvas.SetLeft(MapMarker, point.X - MapMarker.Width / 2);
        Canvas.SetTop(MapMarker, point.Y - MapMarker.Height / 2);
        MapMarker.Visibility = Visibility.Visible;
        CoordinateText.Text = $"Selected grid coordinate: X {_selectedCoordinate.X:0.0}, Y {_selectedCoordinate.Y:0.0} (prototype grid)";
    }

    private void PredictButton_Click(object sender, RoutedEventArgs e)
    {
        var input = new OutagePredictionRequest(
            _selectedCoordinate,
            (StormCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Moderate wind and rain",
            (HomeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Residential",
            MedicalCheck.IsChecked == true,
            AccessCheck.IsChecked == true,
            CriticalCheck.IsChecked == true);
        _currentPrediction = _predictionService.Predict(input);
        RiskValue.Text = _currentPrediction.RiskBand;
        RiskConfidence.Text = $"Confidence: {_currentPrediction.ConfidenceLabel}";
        RestorationValue.Text = _currentPrediction.RestorationRange;
        FactorsText.Text = _currentPrediction.Explanation;
        GuidanceText.Text = _currentPrediction.PreparationGuidance;
        EligibilityText.Text = SmsCheck.IsChecked == true || EmailCheck.IsChecked == true
            ? "Eligible for prototype alerts based on your selected preference. Channel delivery is not connected."
            : "No alert channel selected. Enable SMS or email above to indicate a preference.";
        ChatResponse.Text = "I can explain this planning estimate and help you prepare. This assistant is a prototype and has no live outage information.";
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        ContentScroller.ScrollToTop();
        StormCombo.Focus();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var question = ChatInput.Text.Trim();
        if (question.Length == 0) return;
        ChatResponse.Text = _chatService.GetResponse(question, _currentPrediction);
        ChatInput.Clear();
    }
}
