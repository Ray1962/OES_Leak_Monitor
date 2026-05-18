using System;
using System.Windows;

namespace OES_Leak_Monitor;

/// <summary>Modal prompt for a Golden Run baseline capture: a recipe name plus an
/// explicit leak-free confirmation. The capture itself runs in the Leak Monitor panel.</summary>
public partial class GoldenRunCaptureDialog : Window
{
    public GoldenRunCaptureDialog(string suggestedName, double captureSeconds)
    {
        InitializeComponent();
        NameBox.Text = string.IsNullOrWhiteSpace(suggestedName) ? "Recipe 1" : suggestedName;
        NameBox.SelectAll();
        DurationText.Text =
            $"Capture averages the ratios for about {captureSeconds:F0} seconds — " +
            "keep acquisition running and the process steady.";
        NameBox.TextChanged += (_, _) => UpdateOkState();
    }

    /// <summary>The recipe / baseline name the operator entered.</summary>
    public string RunName => NameBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e) => UpdateOkState();

    private void UpdateOkState() =>
        OkButton.IsEnabled = ConfirmCheck.IsChecked == true &&
                             !string.IsNullOrWhiteSpace(NameBox.Text);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
