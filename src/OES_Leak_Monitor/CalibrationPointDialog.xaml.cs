using System.Globalization;
using System.Windows;

namespace OES_Leak_Monitor;

/// <summary>Modal prompt for one leak-rate calibration point: the known leak rate (mbar·L/s),
/// an optional label, and an explicit confirmation that the leak is applied. The averaging
/// itself runs in the Leak Calibration panel.</summary>
public partial class CalibrationPointDialog : Window
{
    public CalibrationPointDialog(string suggestedLabel)
    {
        InitializeComponent();
        LabelBox.Text = suggestedLabel ?? "";
        LeakRateBox.TextChanged += (_, _) => UpdateOkState();
    }

    /// <summary>Parsed known leak rate, mbar·L/s. Valid only when the dialog returned true.</summary>
    public double LeakRate { get; private set; }

    /// <summary>The leak-element label the operator entered (may be empty).</summary>
    public string Label => LabelBox.Text.Trim();

    private bool TryParseRate(out double rate) =>
        double.TryParse(LeakRateBox.Text.Trim(),
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out rate) && rate > 0;

    private void Confirm_Click(object sender, RoutedEventArgs e) => UpdateOkState();

    private void UpdateOkState()
    {
        bool rateOk = TryParseRate(out _);
        ParseHint.Visibility = LeakRateBox.Text.Length > 0 && !rateOk
            ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = rateOk && ConfirmCheck.IsChecked == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseRate(out double rate)) { UpdateOkState(); return; }
        LeakRate = rate;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
