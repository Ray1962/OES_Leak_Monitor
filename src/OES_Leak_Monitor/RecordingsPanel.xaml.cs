using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using OxyPlot.Axes;

namespace OES_Leak_Monitor;

public partial class RecordingsPanel : UserControl
{
    public RecordingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The wavelength box commits on LostFocus (see the binding); Enter commits too, so a
    /// typed wavelength applies without having to click away.
    /// </summary>
    private void WavelengthBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    /// <summary>
    /// DataGrid.SelectedItems isn't bindable directly. Project the first two
    /// selected rows into the VM (primary, compare) and trigger a reload.
    /// </summary>
    private void GroupsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not RecordingsViewModel vm) return;
        var picked = GroupsGrid.SelectedItems.OfType<RecordingGroup>().ToList();
        var primary = picked.ElementAtOrDefault(0);
        var compare = picked.ElementAtOrDefault(1);
        vm.SetSelection(primary, compare);
    }

    /// <summary>
    /// Left-click on the line plot (no modifiers) → load the frame spectrum at
    /// the clicked elapsed time. OxyPlot's tracker still fires; we don't mark
    /// the event Handled so the existing behavior stays intact.
    /// </summary>
    private void MainPlotView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not RecordingsViewModel vm) return;
        if (vm.ViewMode != RecordingsViewMode.Line) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (sender is not OxyPlot.Wpf.PlotView view) return;
        if (view.Model is not OxyPlot.PlotModel model) return;

        var xAxis = model.Axes.OfType<LinearAxis>()
                              .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
        if (xAxis is null) return;

        var pos = e.GetPosition(view);
        var elapsed = xAxis.InverseTransform(pos.X);
        vm.ShowFrameAt(elapsed);
    }
}
