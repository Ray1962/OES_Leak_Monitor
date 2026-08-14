using System;
using System.Windows;

namespace OES_Leak_Monitor;

/// <summary>Modal prompt for a Golden Run baseline capture: a recipe name plus an
/// explicit leak-free confirmation. The capture itself runs in the Leak Monitor panel.</summary>
public partial class GoldenRunCaptureDialog : Window
{
    private readonly Func<string, GoldenRun?> _findRun;

    /// <param name="findRun">Looks up a stored Golden Run by name, so the dialog can say that
    /// this capture would replace one before the operator commits a minute to it.</param>
    public GoldenRunCaptureDialog(string suggestedName, double captureSeconds,
                                  Func<string, GoldenRun?>? findRun = null)
    {
        InitializeComponent();
        _findRun = findRun ?? (_ => null);
        NameBox.Text = string.IsNullOrWhiteSpace(suggestedName) ? "Recipe 1" : suggestedName;
        NameBox.SelectAll();
        DurationText.Text =
            $"Capture averages the ratios for about {captureSeconds:F0} seconds — " +
            "keep acquisition running and the process steady.";
        NameBox.TextChanged += (_, _) => UpdateOkState();
        UpdateOkState();
    }

    /// <summary>Names the run this capture would replace. The name is editable, so it is
    /// re-checked on every keystroke rather than once at open.</summary>
    private void UpdateReplaceNotice()
    {
        var existing = _findRun(NameBox.Text.Trim());
        if (existing is null)
        {
            ReplaceNotice.Visibility = Visibility.Collapsed;
            return;
        }
        ReplaceText.Text =
            $"This will replace the stored “{existing.Name}” — captured " +
            $"{existing.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}, " +
            $"{existing.Baselines.Count} ratio baseline(s). The old one cannot be recovered.";
        ReplaceNotice.Visibility = Visibility.Visible;
    }

    /// <summary>The recipe / baseline name the operator entered.</summary>
    public string RunName => NameBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e) => UpdateOkState();

    private void UpdateOkState()
    {
        OkButton.IsEnabled = ConfirmCheck.IsChecked == true &&
                             !string.IsNullOrWhiteSpace(NameBox.Text);
        UpdateReplaceNotice();
    }

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
