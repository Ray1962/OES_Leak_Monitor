using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OES_Leak_Monitor;

/// <summary>Leak Monitor tab: composite alarm banner, per-ratio rows, and the trend chart.</summary>
public partial class LeakMonitorPanel : UserControl
{
    public LeakMonitorPanel() => InitializeComponent();

    /// <summary>Toggles the clicked row as the one whose thresholds the chart draws (#104).</summary>
    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LeakMonitorViewModel vm && (sender as FrameworkElement)?.DataContext is RatioViewModel row)
        {
            vm.ToggleSelection(row);
            Focus();   // so Esc reaches Panel_KeyDown
        }
    }

    private void Panel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is LeakMonitorViewModel vm)
        {
            vm.ClearSelection();
            e.Handled = true;
        }
    }
}
